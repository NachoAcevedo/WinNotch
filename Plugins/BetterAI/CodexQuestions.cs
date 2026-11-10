using System.IO;
using System.Text;
using System.Text.Json;

namespace PersonalCards;

internal static class CodexQuestions
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Paths = new();
    public static Task<string?> ReadAsync(string threadId) => Task.Run(() =>
    {
        try
        {
            if (!Paths.TryGetValue(threadId, out var path) || path == null)
            {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                string sessions = Path.Combine(home, "sessions");
                path = Directory.Exists(sessions) ? Directory.EnumerateFiles(sessions, "*" + threadId + ".jsonl", SearchOption.AllDirectories).FirstOrDefault() : null;
                if (path != null) Paths[threadId] = path;
            }
            if (path == null) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - 1024 * 1024);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            if (start > 0) reader.ReadLine();
            string? pending = null, line;
            while ((line = reader.ReadLine()) != null)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("payload", out var payload)) continue;
                    if (payload.TryGetProperty("role", out var role) && role.GetString() == "user") { pending = null; continue; }
                    if (root.TryGetProperty("metadata", out var metadata) && metadata.TryGetProperty("client_authored", out var authored) && authored.ValueKind == JsonValueKind.True
                        && payload.TryGetProperty("type", out var kind) && kind.GetString() == "function_call_output" && payload.GetRawText().Contains("codex_delegation", StringComparison.Ordinal))
                    { pending = null; continue; }
                    if (payload.TryGetProperty("phase", out var phase) && phase.GetString() == "final_answer") { pending = null; continue; }
                    if (!payload.TryGetProperty("name", out var tool)) continue;
                    string name = tool.GetString() ?? "";
                    if (name is not ("request_user_input_async" or "request_user_input") && !name.EndsWith(".request_user_input_async", StringComparison.Ordinal) && !name.EndsWith(".request_user_input", StringComparison.Ordinal)) continue;
                    if (!payload.TryGetProperty("arguments", out var args)) continue;
                    using var arguments = JsonDocument.Parse(args.GetString() ?? "{}");
                    if (!arguments.RootElement.TryGetProperty("questions", out var questions)) continue;
                    var text = new StringBuilder("Codex te pregunta:\n\n");
                    foreach (var question in questions.EnumerateArray())
                    {
                        if (question.TryGetProperty("title", out var title)) text.AppendLine(title.GetString());
                        else if (question.TryGetProperty("question", out title)) text.AppendLine(title.GetString());
                        if (question.TryGetProperty("options", out var options))
                        {
                            int index = 1;
                            foreach (var option in options.EnumerateArray())
                            {
                                string? label = option.ValueKind == JsonValueKind.String ? option.GetString() : option.TryGetProperty("label", out var value) ? value.GetString() : null;
                                if (label != null) text.AppendLine($"{index++}. {label}");
                            }
                        }
                        text.AppendLine();
                    }
                    text.Append("Respondé en el campo de abajo.");
                    pending = text.ToString();
                }
                catch (JsonException) { }
            }
            return pending;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    });
}
