using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace PersonalCards;

// Uses the desktop app's local tools transport. No shell, simulated keys, or window activation.
internal interface ICodexConnection
{
    string ContextThreadId { get; set; }
    Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default);
}

internal sealed class CodexConnection : ICodexConnection
{
    private string? _pipe;
    private int _request;
    public string ContextThreadId { get; set; } = FindContext();

    private static string FindContext()
    {
        string home = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        try
        {
            using var stream = new FileStream(Path.Combine(home, "session_index.jsonl"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            string? line;
            string id = "";
            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("thread_name", out var name)
                        && name.GetString() == "Guardian review") continue;
                    if (doc.RootElement.TryGetProperty("id", out var value)
                        && Guid.TryParse(value.GetString(), out _)) id = value.GetString()!;
                }
                catch (JsonException) { }
            }
            if (id.Length > 0) return id;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return "";
    }

    private async Task<NamedPipeClientStream> ConnectAsync(CancellationToken token)
    {
        var candidates = new List<string>();
        if (_pipe != null) candidates.Add(_pipe);
        var inherited = Environment.GetEnvironmentVariable("CODEX_APP_TOOLS_PIPE_PATH");
        if (!string.IsNullOrWhiteSpace(inherited)) candidates.Add(inherited);
        try { candidates.AddRange(Directory.GetFiles(@"\\.\pipe\", "codex-app-tools-*")); }
        catch (IOException) { }
        foreach (string candidate in candidates.Distinct())
        {
            const string prefix = @"\\.\pipe\";
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var pipe = new NamedPipeClientStream(".", candidate[prefix.Length..], PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(1200, token);
                _pipe = candidate;
                return pipe;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            { pipe.Dispose(); }
        }
        _pipe = null;
        throw new IOException("No pude conectar con Codex. Verificá que la app esté abierta.");
    }

    public async Task<JsonElement> CallAsync(string tool, object arguments, CancellationToken token = default)
    {
        if (ContextThreadId.Length == 0) throw new IOException("Abrí un chat de Codex para poder conectar la tarjeta.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        await using var pipe = await ConnectAsync(timeout.Token);
        int id = Interlocked.Increment(ref _request);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, jsonrpc = "2.0", method = "tools/call",
            @params = new
            {
                arguments, callerSource = "codex", callId = "winnotch-" + Guid.NewGuid().ToString("N"),
                // Root hostId must be omitted: the desktop resolves its own local host.
                @namespace = "codex_app", threadId = ContextThreadId, tool, turnId = Guid.NewGuid().ToString()
            }
        });
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await pipe.WriteAsync(header, timeout.Token);
        await pipe.WriteAsync(bytes, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        await pipe.ReadExactlyAsync(header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > 8388608) throw new IOException("Codex devolvió una respuesta inválida.");
        byte[] response = new byte[length];
        await pipe.ReadExactlyAsync(response, timeout.Token);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.TryGetProperty("error", out var error))
            throw new IOException(error.GetProperty("message").GetString() ?? "Codex no pudo completar la solicitud.");
        var result = root.GetProperty("result");
        string text = string.Join("\n", result.GetProperty("contentItems").EnumerateArray()
            .Where(i => i.TryGetProperty("text", out _)).Select(i => i.GetProperty("text").GetString()));
        if (!result.GetProperty("success").GetBoolean()) throw new IOException(text);
        try { using var parsed = JsonDocument.Parse(text); return parsed.RootElement.Clone(); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { message = text }); }
    }
}

internal sealed record CodexChat(string Id, string Title, string? HostId)
{
    public override string ToString() => Title;
}

internal sealed record CodexPreferences(bool KeepOpenWhileThinking = true, string? ChatId = null);
