namespace MissionControl.Core;

/// <summary>Machine-local defaults; no developer's paths or account settings are distributed.</summary>
public static class WorkspacePaths
{
    public static string DefaultDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("MISSION_CONTROL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(configured)) return Absolute(configured);
        return OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "MissionControl")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MissionControl");
    }

    public static string Absolute(string value)
    {
        if (!Path.IsPathFullyQualified(value) || value.Any(char.IsControl))
            throw new ArgumentException("Use an absolute local path without control characters.");
        return Path.GetFullPath(value);
    }

    public static string ResourcesDirectory(string executableDirectory)
    {
        var directory = new DirectoryInfo(executableDirectory);
        return directory.Name == "MacOS" && directory.Parent?.Name == "Contents"
            ? Path.Combine(directory.Parent.FullName, "Resources") : directory.FullName;
    }

    public static string QuoteShellArgument(string value, bool windows) =>
        "'" + value.Replace("'", windows ? "''" : "'\\''", StringComparison.Ordinal) + "'";

    public static void CreatePrivateDirectory(string path)
    {
        // An owner-only parent protects SQLite, WAL and backups together.
        // Never chmod an existing user-selected parent or follow a symlink to tighten its mode.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(path);
        else Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
