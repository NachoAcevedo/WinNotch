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

public sealed record AgentReply(string Provider, string Text, DateTime UpdatedUtc);

public static class AgentInbox
{
    public const string ClaudeDesktop = "Claude Desktop";
    public static IReadOnlyList<string> Providers { get; } = Array.AsReadOnly(new[] { "Codex", "Claude", ClaudeDesktop });

    public static AgentReply? Parse(string provider, string json)
    {
        if (!Providers.Contains(provider)) return null;
        using var document = JsonDocument.Parse(json);
        var payload = document.RootElement;
        bool stopEvent = payload.TryGetProperty("hook_event_name", out var hookName) && hookName.GetString() == "Stop";
        if (provider == "Codex" && !stopEvent && (!payload.TryGetProperty("type", out var kind) || kind.GetString() != "agent-turn-complete")) return null;
        if (provider == "Claude" && payload.TryGetProperty("hook_event_name", out var hook) && hook.GetString() != "Stop") return null;
        if (provider == ClaudeDesktop && !stopEvent) return null;
        var key = provider == "Codex" && !stopEvent ? "last-assistant-message" : "last_assistant_message";
        if (!payload.TryGetProperty(key, out var text) || text.ValueKind != JsonValueKind.String) return null;
        var message = text.GetString();
        if (string.IsNullOrWhiteSpace(message)) return null;
        return new(provider, message.Length > 100000 ? message[..100000] : message, DateTime.UtcNow);
    }
    public static void Import(string provider, string json)
    {
        var reply = Parse(provider, json);
        if (reply != null) LocalCards.Write($"reply-{reply.Provider}.json", reply);
    }
}

public sealed record LocalTaskItem(Guid Id, string Title, bool Done);
