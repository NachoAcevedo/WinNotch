using WinNotch.Services;
using Xunit;

namespace WinNotch.Tests;

public class AgentInboxTests
{
    [Fact]
    public void CodexImportsOnlyCompletedAssistantText()
    {
        var reply = AgentInbox.Parse("Codex", """{"type":"agent-turn-complete","last-assistant-message":"Respuesta lista","input-messages":["No importar"]}""");
        Assert.NotNull(reply);
        Assert.Equal("Respuesta lista", reply.Text);
        Assert.Equal("Codex", reply.Provider);
        Assert.Equal("Respuesta desde hook", AgentInbox.Parse("Codex", """{"hook_event_name":"Stop","last_assistant_message":"Respuesta desde hook"}""")!.Text);
        Assert.Null(AgentInbox.Parse("Codex", """{"type":"approval-requested","last-assistant-message":"no"}"""));
    }
    [Fact]
    public void ClaudeImportsStopOnly()
    {
        Assert.Equal("Listo", AgentInbox.Parse("Claude", """{"hook_event_name":"Stop","last_assistant_message":"Listo"}""")!.Text);
        Assert.Null(AgentInbox.Parse("Claude", """{"hook_event_name":"PreToolUse","last_assistant_message":"no"}"""));
        Assert.Null(AgentInbox.Parse("Unknown", "{}"));
        Assert.Null(AgentInbox.Parse("Claude", """{"hook_event_name":"Stop","last_assistant_message":" "}"""));
    }
    [Fact]
    public void MessageSizeIsBounded()
    {
        var json = "{\"type\":\"agent-turn-complete\",\"last-assistant-message\":\"" + new string('a', 110000) + "\"}";
        Assert.Equal(100000, AgentInbox.Parse("Codex", json)!.Text.Length);
    }
}
