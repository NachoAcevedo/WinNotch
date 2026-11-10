using System.IO;
using System.Text.Json;

namespace WinNotch.Services;

public static class LocalCards
{
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinNotch", "Cards");
    public static void Write<T>(string name, T value)
    {
        Directory.CreateDirectory(DirectoryPath);
        var target = Path.Combine(DirectoryPath, name);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value));
        File.Move(temporary, target, true);
    }
    public static T? Read<T>(string name)
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(DirectoryPath, name))); }
        catch (IOException) { return default; }
        catch (JsonException) { return default; }
        catch (UnauthorizedAccessException) { return default; }
    }
}

public sealed record AgentReply(string Provider, string Text, DateTime UpdatedUtc, string? SessionId = null);

public static class AgentInbox
{
    public static AgentReply? Parse(string provider, string json)
    {
        if (!AgentProviders.All.Contains(provider) || provider == AgentProviders.Gemini) return null;
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement;
        bool stopEvent = payload.TryGetProperty("hook_event_name", out var hookName) && hookName.GetString() == "Stop";
        bool isCodex = provider is "Codex" or AgentProviders.CodexVsCode;
        if (isCodex && !stopEvent && (!payload.TryGetProperty("type", out var kind) || kind.GetString() != "agent-turn-complete")) return null;
        if (provider == "Claude" && payload.TryGetProperty("hook_event_name", out var hook) && hook.GetString() != "Stop") return null;
        if (provider == AgentProviders.ClaudeDesktop && !stopEvent) return null;
        var key = isCodex && !stopEvent ? "last-assistant-message" : "last_assistant_message";
        if (!payload.TryGetProperty(key, out var text) || text.ValueKind != JsonValueKind.String) return null;
        var message = text.GetString();
        if (string.IsNullOrWhiteSpace(message)) return null;
        var sessionId = new[] { "thread_id", "session_id", "thread-id" }
            .Select(name => payload.TryGetProperty(name, out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString() : null)
            .FirstOrDefault(id => Guid.TryParseExact(id, "D", out _));
        if (provider == AgentProviders.CodexVsCode && sessionId == null) return null;
        return new(provider, message.Length > 100000 ? message[..100000] : message, DateTime.UtcNow, sessionId);
    }
    public static void Import(string provider, string json)
    {
        var reply = Parse(provider, json);
        if (reply != null) LocalCards.Write($"reply-{reply.Provider}.json", reply);
    }
}

public sealed record LocalTaskItem(Guid Id, string Title, bool Done);
