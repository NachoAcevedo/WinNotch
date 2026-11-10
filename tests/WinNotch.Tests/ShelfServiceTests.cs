using System.IO;
using WinNotch.Services;
using Xunit;

namespace WinNotch.Tests;

public class ShelfServiceTests
{
    [Fact]
    public void StagingPersistsReferencesWithoutMovingOrDeletingOriginals()
    {
        var folder = Path.Combine(Path.GetTempPath(), "WinNotch-test-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var original = Path.Combine(folder, "original.txt");
        var state = Path.Combine(folder, "shelf.json");
        File.WriteAllText(original, "contenido original");
        var shelf = new ShelfService(state);
        shelf.AddFiles(new[] { original, original, Path.Combine(folder, "missing.txt") });
        Assert.Single(shelf.Items);
        Assert.Equal("contenido original", File.ReadAllText(original));
        var reloaded = new ShelfService(state);
        Assert.Single(reloaded.Items);
        reloaded.Clear();
        Assert.Empty(new ShelfService(state).Items);
        Assert.True(File.Exists(original));
        File.Delete(original);
        File.Delete(state);
        Directory.Delete(folder);
    }
}
