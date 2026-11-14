namespace WinNotch.Services;

internal static class AgentProviders
{
    public const string CodexVsCode = "Codex (VS Code)";
    public const string ClaudeDesktop = "Claude Desktop";
    public const string Gemini = "Gemini (Antigravity)";
    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(new[] { "Codex", "Claude", CodexVsCode, ClaudeDesktop, Gemini });
}
