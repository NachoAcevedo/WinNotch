using WinNotch.Services;

try
{
    if (args.Length < 1 || !AgentInbox.Providers.Contains(args[0])) return 1;
    var json = args.Length >= 2 ? args[1] : new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false, true)).ReadToEnd();
    if (args[0] == AgentInbox.Gemini)
    {
        Console.Write("{\"decision\":\"stop\"}");
        if (AntigravityInbox.ReadReply(json) is { } reply)
            LocalCards.Write($"reply-{reply.Provider}.json", reply);
    }
    else
    {
        AgentInbox.Import(args[0], json);
        Console.Write("{}");
    }
    return 0;
}
catch { return 1; }
