using PCAutobackup.Core;

namespace PCAutobackup.Tests;

/// <summary>Regression checks for the Desktop/Tray/packaging lane that need no window and touch only temporary directories.</summary>
public sealed class AuditDesktopTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "PCAutobackup-tests", Guid.NewGuid().ToString("N"));
    public AuditDesktopTests() => Directory.CreateDirectory(root);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"Version\":2}")]
    public async Task EmptyOrUnsupportedConfigurationIsNotAnIoFailure(string json)
    {
        // The tray reads its language inside a window callback, so its exception filter must name this type.
        var path = Path.Combine(root, "configuration.json");
        await File.WriteAllTextAsync(path, json);

        await Assert.ThrowsAsync<InvalidDataException>(() => ConfigurationStore.LoadAsync(path));
        Assert.False(typeof(IOException).IsAssignableFrom(typeof(InvalidDataException)));
    }
}
