namespace MissionControl.Tests;

/// <summary>Values that match the machine running the tests, so the observer treats test sources as native.</summary>
internal static class TestPlatform
{
    public static string Environment => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";

    /// <summary>An absolute project folder that never needs to exist.</summary>
    public static string Work => OperatingSystem.IsWindows() ? @"C:\work" : "/work";
}
