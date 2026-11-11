using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PersonalCards;

/// <summary>
/// El agente de VS Code (Claude Code o Codex) con la misma interfaz que la conexión de la app de
/// Codex, para que la tarjeta lo muestre con el mismo panel: los chats y las respuestas se leen
/// de los transcripts locales del agente y los mensajes se envían en segundo plano con su CLI,
/// sin traer VS Code al frente ni simular teclas.
/// </summary>
internal sealed class VsCodeAgentConnection : ICodexConnection
{
    // Sin escrituras al transcript durante este tiempo, un turno sin respuesta final se da por cortado.
    private static readonly TimeSpan StaleTurn = TimeSpan.FromSeconds(90);

    private readonly TranscriptAgent _agent;

    public VsCodeAgentConnection(TranscriptAgent agent) => _agent = agent;

    public string ContextThreadId { get; set; } = "";

    public Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default) => Task.Run(() =>
    {
        var args = JsonSerializer.SerializeToElement(arguments);
        return tool switch
        {
            "list_threads" => ListThreads(args),
            "read_thread" => ReadThread(args),
            "send_message_to_thread" => Send(Text(args, "threadId"), Text(args, "prompt")!),
            "create_thread" => Send(null, Text(args, "prompt")!),
            _ => throw new IOException(_agent.Name + " no admite la operación " + tool + ".")
        };
    }, token);

    private static string? Text(JsonElement args, string name) =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private bool IsBusy(string id, AgentTranscript? transcript)
    {
        if (_agent.Runs.TryGetValue(id, out var run) && !run.HasExited) return true;
        return transcript is { AwaitingReply: true } && DateTime.UtcNow - transcript.LastWriteUtc < StaleTurn;
    }

    private JsonElement ListThreads(JsonElement args)
    {
        int limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out int n) ? n : 50;
        int offset = int.TryParse(Text(args, "cursor"), out int c) ? c : 0;
        var threads = new List<object>();
        int index = 0;
        bool more = false;
        foreach (var file in _agent.SessionFiles())
        {
            if (index++ < offset) continue;
            if (threads.Count >= limit) { more = true; break; }
            var transcript = _agent.Transcript(file);
            if (transcript.Title == null) continue;
            string id = _agent.IdOf(file);
            threads.Add(new
            {
                id, title = _agent.TitleOf(id) ?? transcript.Title,
                kind = "codex", // el panel compartido solo lista este tipo
                hostId = "local",
                status = IsBusy(id, transcript) ? "active" : "idle",
                updatedAt = transcript.LastWriteUtc.ToString("O")
            });
        }
        // Un chat recién creado puede no tener transcript todavía: se lista igual mientras responde.
        foreach (var pending in _agent.Runs.Where(r => r.Value.Created && !r.Value.HasExited && _agent.Find(r.Key) == null))
            threads.Insert(0, new { id = pending.Key, title = "Nuevo chat", kind = "codex", hostId = "local", status = "active", updatedAt = pending.Value.StartedUtc.ToString("O") });
        return JsonSerializer.SerializeToElement(new { threads, nextCursor = more ? (index - 1).ToString() : null });
    }

    private JsonElement ReadThread(JsonElement args)
    {
        string id = Text(args, "threadId")!;
        int limit = args.TryGetProperty("turnLimit", out var l) && l.TryGetInt32(out int n) ? n : 3;
        var transcript = _agent.Find(id);
        bool busy = IsBusy(id, transcript);
        var turns = new List<object>();
        if (!busy && _agent.Runs.TryGetValue(id, out var run) && run.Error != null)
            turns.Add(Turn("completed", null, new[] { new AgentChatReply(_agent.Name + " no pudo completar el envío:\n" + run.Error, true) }));
        var history = transcript?.Turns ?? new List<AgentTurnSnapshot>();
        for (int i = history.Count - 1; i >= 0 && turns.Count < limit; i--)
        {
            bool last = i == history.Count - 1;
            turns.Add(Turn(last && busy ? "inProgress" : "completed", history[i].UserText, history[i].Replies));
        }
        return JsonSerializer.SerializeToElement(new { thread = new { status = busy ? "active" : "idle" }, turns });
    }

    private static object Turn(string status, string? user, IEnumerable<AgentChatReply> replies)
    {
        var items = new List<object>();
        if (user != null) items.Add(new { type = "userMessage", content = new[] { new { text = user } } });
        foreach (var reply in replies)
            items.Add(new { type = "agentMessage", text = reply.Text, phase = reply.Final ? "final_answer" : "commentary" });
        return new { status, items };
    }

    private JsonElement Send(string? id, string prompt)
    {
        if (id != null && _agent.Runs.TryGetValue(id, out var running) && !running.HasExited)
            throw new IOException(_agent.Name + " todavía está respondiendo en ese chat.");
        string cwd = (id == null ? null : _agent.Find(id)?.Cwd) ?? _agent.DefaultDirectory();
        if (!Directory.Exists(cwd)) cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (start, knownId) = _agent.StartInfo(id, cwd);
        start.WorkingDirectory = cwd;
        start.UseShellExecute = false; start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        start.StandardInputEncoding = new UTF8Encoding(false);
        start.StandardOutputEncoding = start.StandardErrorEncoding = Encoding.UTF8;
        var process = Process.Start(start) ?? throw new IOException("No pude iniciar " + _agent.Name + ".");
        var run = new AgentRun(process, created: id == null, _agent.ReadNewId);
        // El mensaje va por la entrada estándar: sin límites de largo ni problemas de comillas.
        process.StandardInput.Write(prompt);
        process.StandardInput.Close();
        string? threadId = id ?? knownId ?? run.WaitForId(TimeSpan.FromSeconds(20));
        if (threadId != null) _agent.Runs[threadId] = run;
        return JsonSerializer.SerializeToElement(new { threadId, hostId = "local" });
    }
}

internal sealed record AgentChatReply(string Text, bool Final);

internal sealed record AgentTurnSnapshot(string UserText, IReadOnlyList<AgentChatReply> Replies);

/// <summary>Un envío en segundo plano; guarda el error si el CLI termina mal.</summary>
internal sealed class AgentRun
{
    private readonly Process _process;
    private readonly StringBuilder _output = new();
    private readonly Func<string, string?>? _readId;
    private readonly TaskCompletionSource<string?> _id = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Created { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public string? Error { get; private set; }
    public bool HasExited => _process.HasExited;

    public AgentRun(Process process, bool created, Func<string, string?>? readId)
    {
        _process = process;
        _readId = readId;
        Created = created;
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) => { Append(e.Data); if (e.Data != null && _readId?.Invoke(e.Data) is { } id) _id.TrySetResult(id); };
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Exited += (_, _) =>
        {
            process.WaitForExit();
            _id.TrySetResult(null);
            if (process.ExitCode == 0) return;
            string text;
            lock (_output) text = _output.ToString().Trim();
            Error = text.Length == 0 ? "terminó con código " + process.ExitCode + "." : text.Length > 400 ? "…" + text[^400..] : text;
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public string? WaitForId(TimeSpan timeout) => _id.Task.Wait(timeout) ? _id.Task.Result : null;

    private void Append(string? line)
    {
        if (line == null) return;
        lock (_output) { if (_output.Length < 20000) _output.AppendLine(line); }
    }
}

/// <summary>Lo propio de cada agente: dónde guarda las sesiones, cómo leerlas y cómo invocar su CLI.</summary>
internal abstract class TranscriptAgent
{
    public static readonly TranscriptAgent ClaudeCode = new ClaudeCodeAgent();
    public static readonly TranscriptAgent Codex = new CodexVsCodeAgent();

    private readonly ConcurrentDictionary<string, AgentTranscript> _transcripts = new();
    public readonly ConcurrentDictionary<string, AgentRun> Runs = new();

    public abstract string Name { get; }
    public abstract IEnumerable<FileInfo> SessionFiles();
    public abstract string IdOf(FileInfo file);
    protected abstract AgentTranscript Open(string path);
    /// <summary>Arranque del CLI; si la sesión es nueva y el id se conoce de antemano, lo devuelve.</summary>
    public abstract (ProcessStartInfo Start, string? KnownId) StartInfo(string? id, string cwd);
    /// <summary>Id de una sesión nueva leído de una línea de la salida del CLI.</summary>
    public virtual string? ReadNewId(string line) => null;
    public virtual string? TitleOf(string id) => null;

    public AgentTranscript Transcript(FileInfo file)
    {
        var transcript = _transcripts.GetOrAdd(IdOf(file), _ => Open(file.FullName));
        transcript.Update();
        return transcript;
    }

    public AgentTranscript? Find(string id)
    {
        if (_transcripts.TryGetValue(id, out var known)) { known.Update(); return known.Exists ? known : null; }
        var file = SessionFiles().FirstOrDefault(f => IdOf(f) == id);
        return file == null ? null : Transcript(file);
    }

    public string DefaultDirectory() =>
        SessionFiles().Take(10).Select(f => Transcript(f).Cwd).FirstOrDefault(cwd => cwd != null)
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    protected static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    protected static string? FindOnPath(params string[] names) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(dir => names.Select(name => Path.Combine(dir, name))).FirstOrDefault(File.Exists);
}

internal sealed class ClaudeCodeAgent : TranscriptAgent
{
    private static readonly string ProjectsDir = Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") ?? Path.Combine(Home, ".claude"), "projects");

    public override string Name => "Claude Code";

    public override IEnumerable<FileInfo> SessionFiles()
    {
        if (!Directory.Exists(ProjectsDir)) return Array.Empty<FileInfo>();
        // Solo el primer nivel de cada proyecto: las subcarpetas guardan subagentes.
        return new DirectoryInfo(ProjectsDir).EnumerateDirectories()
            .SelectMany(project => project.EnumerateFiles("*.jsonl", SearchOption.TopDirectoryOnly))
            .OrderByDescending(file => file.LastWriteTimeUtc);
    }

    public override string IdOf(FileInfo file) => Path.GetFileNameWithoutExtension(file.Name);

    protected override AgentTranscript Open(string path) => new ClaudeTranscript(path);

    public override (ProcessStartInfo, string?) StartInfo(string? id, string cwd)
    {
        // Primero el que trae la extensión de VS Code (es el que usa VS Code y se actualiza con
        // ella); un CLI global puede estar viejo y no soportar el modelo actual.
        string extensions = Path.Combine(Home, ".vscode", "extensions");
        string? bundled = Directory.Exists(extensions)
            ? new DirectoryInfo(extensions).EnumerateDirectories("anthropic.claude-code-*")
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Select(d => Path.Combine(d.FullName, "resources", "native-binary", "claude.exe"))
                .FirstOrDefault(File.Exists)
            : null;
        string exe = bundled
            ?? FindOnPath("claude.exe", "claude.cmd")
            ?? throw new IOException("No encontré el CLI de Claude Code (claude). Instalalo para enviar desde la tarjeta.");
        var start = new ProcessStartInfo(exe);
        string session = id ?? Guid.NewGuid().ToString();
        start.ArgumentList.Add("-p");
        start.ArgumentList.Add(id == null ? "--session-id" : "--resume");
        start.ArgumentList.Add(session);
        return (start, session);
    }
}

internal sealed class CodexVsCodeAgent : TranscriptAgent
{
    private static readonly string CodexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Home, ".codex");
    private static readonly Regex IdPattern = new("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase);
    private Dictionary<string, string> _names = new();
    private DateTime _namesStamp;

    public override string Name => "Codex";

    public override IEnumerable<FileInfo> SessionFiles()
    {
        string sessions = Path.Combine(CodexHome, "sessions");
        if (!Directory.Exists(sessions)) return Array.Empty<FileInfo>();
        return new DirectoryInfo(sessions).EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(file => file.LastWriteTimeUtc);
    }

    public override string IdOf(FileInfo file) => IdPattern.Match(Path.GetFileNameWithoutExtension(file.Name)).Value;

    protected override AgentTranscript Open(string path) => new CodexTranscript(path);

    /// <summary>Los nombres de los chats que pone Codex están en session_index.jsonl.</summary>
    public override string? TitleOf(string id)
    {
        try
        {
            var index = new FileInfo(Path.Combine(CodexHome, "session_index.jsonl"));
            if (index.Exists && index.LastWriteTimeUtc != _namesStamp)
            {
                var names = new Dictionary<string, string>();
                using var stream = new FileStream(index.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        if (doc.RootElement.TryGetProperty("id", out var i) && doc.RootElement.TryGetProperty("thread_name", out var name) && name.GetString() is { Length: > 0 } text)
                            names[i.GetString() ?? ""] = text;
                    }
                    catch (JsonException) { }
                }
                _names = names; _namesStamp = index.LastWriteTimeUtc;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return _names.TryGetValue(id, out var title) ? title : null;
    }

    public override (ProcessStartInfo, string?) StartInfo(string? id, string cwd)
    {
        var start = new ProcessStartInfo(CodexExecutable());
        start.ArgumentList.Add("exec");
        if (id != null) { start.ArgumentList.Add("resume"); }
        start.ArgumentList.Add("--json");
        start.ArgumentList.Add("--skip-git-repo-check");
        if (id != null) start.ArgumentList.Add(id);
        start.ArgumentList.Add("-");
        return (start, null);
    }

    // codex exec --json anuncia la sesión nueva con {"type":"thread.started","thread_id":"…"}.
    public override string? ReadNewId(string line)
    {
        if (!line.Contains("thread_id", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.TryGetProperty("thread_id", out var id) ? id.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static string CodexExecutable()
    {
        // El CLI que trae la extensión de VS Code (la versión más nueva instalada) o uno del PATH.
        string extensions = Path.Combine(Home, ".vscode", "extensions");
        var bundled = Directory.Exists(extensions)
            ? new DirectoryInfo(extensions).EnumerateDirectories("openai.chatgpt-*")
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .Select(d => Path.Combine(d.FullName, "bin", "windows-x86_64", "codex.exe"))
                .FirstOrDefault(File.Exists)
            : null;
        return bundled ?? FindOnPath("codex.exe", "codex.cmd")
            ?? throw new IOException("No encontré el CLI de Codex. Instalá la extensión de Codex en VS Code.");
    }
}

/// <summary>
/// Lectura incremental de un transcript: solo se procesa lo que se agregó desde la última vez,
/// así refrescar la lista seguido no relee archivos de varios MB.
/// </summary>
internal abstract class AgentTranscript
{
    private const int KeepTurns = 5;
    private readonly string _path;
    private readonly object _gate = new();
    private long _offset;
    private byte[] _partial = Array.Empty<byte>();
    private readonly List<Turn> _turns = new();
    protected string? FirstPrompt;

    protected AgentTranscript(string path) => _path = path;

    public bool Exists { get; private set; }
    public string? Cwd { get; protected set; }
    public DateTime LastWriteUtc { get; private set; }
    public virtual string? Title => FirstPrompt;

    protected sealed class Turn
    {
        public string? Key;
        public string UserText = "";
        public bool Finished;
        // Texto por mensaje del agente: un mensaje puede llegar partido en varias líneas.
        public readonly List<(string Id, StringBuilder Text, bool Final)> Messages = new();
    }

    protected Turn? Current => _turns.Count > 0 ? _turns[^1] : null;

    protected Turn StartTurn(string? key, string userText)
    {
        var turn = new Turn { Key = key, UserText = userText };
        _turns.Add(turn);
        if (_turns.Count > KeepTurns) _turns.RemoveAt(0);
        return turn;
    }

    protected static void AddReply(Turn turn, string id, string text, bool final)
    {
        int index = id.Length == 0 ? -1 : turn.Messages.FindIndex(x => x.Id == id);
        if (index < 0) { turn.Messages.Add((id, new StringBuilder(text), final)); return; }
        var existing = turn.Messages[index];
        if (text.Length > 0) { if (existing.Text.Length > 0) existing.Text.Append("\n\n"); existing.Text.Append(text); }
        turn.Messages[index] = (existing.Id, existing.Text, existing.Final || final);
    }

    public List<AgentTurnSnapshot> Turns
    {
        get
        {
            lock (_gate)
                return _turns.Select(t => new AgentTurnSnapshot(t.UserText,
                    t.Messages.Select(m => new AgentChatReply(m.Text.ToString().Trim(), m.Final)).Where(r => r.Text.Length > 0).ToList())).ToList();
        }
    }

    /// <summary>El último pedido todavía no tiene respuesta final.</summary>
    public bool AwaitingReply
    {
        get { lock (_gate) return _turns.Count > 0 && !_turns[^1].Finished; }
    }

    public void Update()
    {
        lock (_gate)
        {
            var info = new FileInfo(_path);
            Exists = info.Exists;
            if (!info.Exists) return;
            if (info.Length < _offset) { _offset = 0; _partial = Array.Empty<byte>(); _turns.Clear(); FirstPrompt = null; Cwd = null; Reset(); }
            LastWriteUtc = info.LastWriteTimeUtc;
            if (info.Length == _offset) return;
            try
            {
                using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                stream.Seek(_offset, SeekOrigin.Begin);
                var buffer = new byte[stream.Length - _offset];
                int read = 0;
                while (read < buffer.Length)
                {
                    int count = stream.Read(buffer, read, buffer.Length - read);
                    if (count == 0) break;
                    read += count;
                }
                _offset += read;
                byte[] data = _partial.Length == 0 ? buffer : _partial.Concat(buffer.Take(read)).ToArray();
                int length = _partial.Length + read;
                int lineStart = 0;
                for (int i = 0; i < length; i++)
                {
                    if (data[i] != (byte)'\n') continue;
                    Line(data.AsSpan(lineStart, i - lineStart));
                    lineStart = i + 1;
                }
                _partial = data.AsSpan(lineStart, length - lineStart).ToArray();
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void Line(ReadOnlySpan<byte> line)
    {
        if (!Wanted(line)) return;
        try
        {
            using var doc = JsonDocument.Parse(line.ToArray());
            Process(doc.RootElement);
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
        catch (KeyNotFoundException) { }
    }

    /// <summary>Filtro barato antes de parsear: la mayoría de las líneas son metadatos grandes.</summary>
    protected abstract bool Wanted(ReadOnlySpan<byte> line);
    protected abstract void Process(JsonElement root);
    protected virtual void Reset() { }

    protected static string? TitleFrom(string text)
    {
        var command = Regex.Match(text, "<command-name>(.*?)</command-name>");
        if (command.Success)
        {
            var args = Regex.Match(text, "<command-args>(.*?)</command-args>", RegexOptions.Singleline);
            text = command.Groups[1].Value + (args.Success ? " " + args.Groups[1].Value : "");
        }
        text = text.Trim().Split('\n')[0].Trim();
        if (text.Length == 0) return null;
        return text.Length > 70 ? text[..70] + "…" : text;
    }
}

internal sealed class ClaudeTranscript : AgentTranscript
{
    private string? _aiTitle;

    public ClaudeTranscript(string path) : base(path) { }

    public override string? Title => _aiTitle ?? FirstPrompt;

    protected override void Reset() => _aiTitle = null;

    protected override bool Wanted(ReadOnlySpan<byte> line) =>
        line.IndexOf("\"type\":\"user\""u8) >= 0 || line.IndexOf("\"type\":\"assistant\""u8) >= 0 || line.IndexOf("\"type\":\"ai-title\""u8) >= 0;

    protected override void Process(JsonElement root)
    {
        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "ai-title")
        {
            if (root.TryGetProperty("aiTitle", out var title) && !string.IsNullOrWhiteSpace(title.GetString())) _aiTitle = title.GetString();
            return;
        }
        if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) return;
        if (!root.TryGetProperty("message", out var message)) return;
        if (type == "user") User(root, message);
        else if (type == "assistant") Assistant(message);
    }

    private void User(JsonElement root, JsonElement message)
    {
        if (root.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } dir) Cwd = dir;
        if (!message.TryGetProperty("content", out var content)) return;
        string text;
        if (content.ValueKind == JsonValueKind.String) text = content.GetString() ?? "";
        else if (content.ValueKind == JsonValueKind.Array)
        {
            var blocks = content.EnumerateArray().ToArray();
            if (blocks.Any(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result")) return;
            text = string.Join("\n", blocks.Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString()));
        }
        else return;

        bool meta = root.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True;
        string? promptId = root.TryGetProperty("promptId", out var p) ? p.GetString() : null;
        var current = Current;
        if (text.StartsWith("[Request interrupted", StringComparison.Ordinal))
        {
            if (current != null) current.Finished = true;
            return;
        }
        // Las expansiones de skills e imágenes llegan como mensajes "meta" del mismo pedido.
        if (meta || (promptId != null && current?.Key == promptId))
        {
            if (current != null && !meta && text.Length > 0) current.UserText += "\n" + text;
            return;
        }
        StartTurn(promptId, text);
        FirstPrompt ??= TitleFrom(text);
    }

    private void Assistant(JsonElement message)
    {
        if (Current is not { } turn) return;
        string id = message.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "";
        bool final = message.TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String && stop.GetString() == "end_turn";
        if (final) turn.Finished = true;
        string text = message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Join("\n\n", content.EnumerateArray().Where(b => b.TryGetProperty("type", out var bt) && bt.GetString() == "text").Select(b => b.GetProperty("text").GetString()))
            : "";
        AddReply(turn, id, text, final);
    }
}

internal sealed class CodexTranscript : AgentTranscript
{
    public CodexTranscript(string path) : base(path) { }

    protected override bool Wanted(ReadOnlySpan<byte> line) =>
        line.IndexOf("\"type\":\"session_meta\""u8) >= 0 || line.IndexOf("\"type\":\"task_started\""u8) >= 0
        || line.IndexOf("\"type\":\"task_complete\""u8) >= 0 || line.IndexOf("\"type\":\"turn_aborted\""u8) >= 0
        || line.IndexOf("\"type\":\"UserMessage\""u8) >= 0 || line.IndexOf("\"role\":\"assistant\""u8) >= 0;

    protected override void Process(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)) return;
        string? type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        string? kind = payload.TryGetProperty("type", out var k) ? k.GetString() : null;
        if (type == "session_meta")
        {
            if (payload.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } dir) Cwd = dir;
            return;
        }
        if (type == "event_msg")
        {
            string? turnId = payload.TryGetProperty("turn_id", out var tid) ? tid.GetString() : null;
            switch (kind)
            {
                case "task_started":
                    if (Current?.Key != turnId || turnId == null) StartTurn(turnId, "");
                    break;
                case "task_complete":
                case "turn_aborted":
                    if (Current is { } done) done.Finished = true;
                    break;
                case "item_completed" when payload.TryGetProperty("item", out var item)
                    && item.TryGetProperty("type", out var it) && it.GetString() == "UserMessage":
                    string text = item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                        ? string.Join("\n", content.EnumerateArray().Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString()))
                        : "";
                    var turn = Current ?? StartTurn(turnId, "");
                    turn.UserText = turn.UserText.Length == 0 ? text : turn.UserText + "\n" + text;
                    FirstPrompt ??= TitleFrom(text);
                    break;
            }
            return;
        }
        if (type == "response_item" && kind == "message" && payload.TryGetProperty("role", out var role) && role.GetString() == "assistant")
        {
            if (Current is not { } turn) return;
            string text = payload.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
                ? string.Join("\n\n", content.EnumerateArray().Where(b => b.TryGetProperty("text", out _)).Select(b => b.GetProperty("text").GetString()))
                : "";
            bool final = payload.TryGetProperty("phase", out var phase) && phase.GetString() == "final_answer";
            AddReply(turn, payload.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "", text, final);
        }
    }
}
