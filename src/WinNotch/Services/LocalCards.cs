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

public sealed record LocalTaskItem(Guid Id, string Title, bool Done);
