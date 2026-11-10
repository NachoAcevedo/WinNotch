using System.Text;
using System.Text.Json;
using WinNotch.Services;

internal static class AntigravityInbox
{
    private const int MaxTranscriptBytes = 512 * 1024;

    public static AgentReply? ReadReply(string json)
    {
        using var document = JsonDocument.Parse(json);
        var hook = document.RootElement;
        if (ReadString(hook, "terminationReason") != "model_stop" ||
            !hook.TryGetProperty("fullyIdle", out var idle) || idle.ValueKind != JsonValueKind.True ||
            !Guid.TryParse(ReadString(hook, "conversationId"), out var conversationId) ||
            ReadString(hook, "transcriptPath") is not { } path ||
            !Path.IsPathFullyQualified(path) ||
            !path.EndsWith(Path.Combine(conversationId.ToString("D"), ".system_generated", "logs", "transcript.jsonl"),
                StringComparison.OrdinalIgnoreCase))
            return null;

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - MaxTranscriptBytes);
        stream.Position = start;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (start > 0) reader.ReadLine();

        string? message = null;
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var entry = JsonDocument.Parse(line);
                var step = entry.RootElement;
                var type = ReadString(step, "type");
                if (type == "USER_INPUT")
                    message = null;
                else if (type == "PLANNER_RESPONSE" && ReadString(step, "source") == "MODEL" &&
                         ReadString(step, "status") == "DONE" && ReadString(step, "content") is { } content &&
                         !string.IsNullOrWhiteSpace(content))
                    message = content;
            }
            catch (JsonException)
            {
                // The transcript may end with a record still being written.
            }
        }

        return message == null ? null : new AgentReply(
            AgentProviders.Gemini, message[..Math.Min(message.Length, 100000)], DateTime.UtcNow);
    }

    private static string? ReadString(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
