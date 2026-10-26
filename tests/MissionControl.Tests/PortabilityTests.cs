namespace MissionControl.Tests;

public sealed class UnixFactAttribute : FactAttribute
{
    public UnixFactAttribute() { if (OperatingSystem.IsWindows()) Skip = "Unix permissions require a Unix filesystem; run on macOS."; }
}

public sealed class PortabilityTests
{
    [Fact]
    public void BundleResourcesAreSeparateFromExecutablesOnlyInsideMacApp()
    {
        var root = Path.Combine(Path.GetTempPath(), "Example App.app");
        Assert.Equal(Path.Combine(root, "Contents", "Resources"), WorkspacePaths.ResourcesDirectory(Path.Combine(root, "Contents", "MacOS")));
        Assert.Equal(Path.GetFullPath(root), WorkspacePaths.ResourcesDirectory(root));
    }

    [Fact]
    public void CopiedCommandsQuoteSpacesApostrophesAndShellExpansionLiterally()
    {
        const string value = "/Users/O'Brien/$(touch bad)/`secret`";
        Assert.Equal("'/Users/O'\\''Brien/$(touch bad)/`secret`'", WorkspacePaths.QuoteShellArgument(value, false));
        Assert.Equal("'/Users/O''Brien/$(touch bad)/`secret`'", WorkspacePaths.QuoteShellArgument(value, true));
        Assert.Throws<ArgumentException>(() => WorkspacePaths.Absolute("relative/path"));
    }

    [UnixFact]
    public void NewDatabaseDirectoryIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) throw new InvalidOperationException("UnixFact must skip this test on Windows.");
        var root = Path.Combine(Path.GetTempPath(), "mission-control-private-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new WorkspaceStore(Path.Combine(root, "live", "workspace.db"));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(root, "live")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
