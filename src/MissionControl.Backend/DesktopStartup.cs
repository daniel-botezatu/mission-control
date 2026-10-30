using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;

namespace MissionControl.Backend;

/// <summary>
/// Windows start-at-sign-in registration, changed only by an explicit settings operation, never on launch.
/// On macOS people add the app under System Settings > General > Login Items instead.
/// </summary>
internal sealed class DesktopStartup(string dataDirectory, string? shellPath)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MissionControl";

    public static bool Supported => OperatingSystem.IsWindows();
    public bool Enabled => OperatingSystem.IsWindows() && WindowsEnabled();

    public void SetEnabled(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (enabled) throw new PlatformNotSupportedException("Add Mission Control under System Settings > General > Login Items.");
            return;
        }
        if (enabled && (shellPath is null || !File.Exists(shellPath)))
            throw new InvalidOperationException("The installed desktop shell is required to enable startup.");
        SetWindowsEnabled(enabled);
    }

    [SupportedOSPlatform("windows")]
    private bool WindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string command && OwnsWindowsCommand(command);
    }

    [SupportedOSPlatform("windows")]
    private void SetWindowsEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (key.GetValue(ValueName) is string existing && !OwnsWindowsCommand(existing))
            throw new InvalidOperationException("The existing Mission Control startup entry belongs to another data directory. It was preserved.");
        if (enabled) key.SetValue(ValueName, BuildWindowsCommand(shellPath!, dataDirectory), RegistryValueKind.String);
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    [SupportedOSPlatform("windows")]
    private bool OwnsWindowsCommand(string command)
    {
        var pointer = CommandLineToArgvW(command, out var count);
        if (pointer == IntPtr.Zero) return false;
        try
        {
            var arguments = Enumerable.Range(0, count).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, index * IntPtr.Size)) ?? "").ToArray();
            // Both the previous .NET host and the Tauri shell use this same registration and workspace.
            return OwnsArguments(arguments, dataDirectory, StringComparison.OrdinalIgnoreCase);
        }
        finally { LocalFree(pointer); }
    }

    internal static bool OwnsArguments(string[] arguments, string expectedDataDirectory, StringComparison comparison)
    {
        var index = Array.IndexOf(arguments, "--data-dir");
        if (index < 1 || index + 1 >= arguments.Length || !arguments.Contains("--background")) return false;
        try
        {
            return Path.IsPathFullyQualified(arguments[index + 1])
                && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(arguments[index + 1])),
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(expectedDataDirectory)), comparison);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    internal static string BuildWindowsCommand(string executable, string directory) =>
        string.Join(' ', new[] { executable, "--background", "--data-dir", Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) }.Select(QuoteArgument));

    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
            result.Append(character); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string commandLine, out int count);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr pointer);
}
