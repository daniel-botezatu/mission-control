using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32;

namespace MissionControl.Backend;

/// <summary>Registration is only changed by an explicit settings operation, never on launch.</summary>
internal sealed class DesktopStartup(string dataDirectory, string? shellPath)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MissionControl";
    private const string LaunchAgentLabel = "local.missioncontrol.desktop";
    private string LaunchAgentPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    public bool Enabled => OperatingSystem.IsWindows() ? WindowsEnabled()
        : OperatingSystem.IsMacOS() ? MacEnabled() : false;

    public void SetEnabled(bool enabled)
    {
        if (enabled && (shellPath is null || !File.Exists(shellPath)))
            throw new InvalidOperationException("The installed desktop shell is required to enable startup.");
        if (OperatingSystem.IsWindows()) SetWindowsEnabled(enabled);
        else if (OperatingSystem.IsMacOS()) SetMacEnabled(enabled);
        else throw new PlatformNotSupportedException("Startup registration is supported on Windows and macOS.");
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

    // Shown as on only when the login item exists, is ours, and is not switched off in
    // System Settings > General > Login Items (which launchd records as a disabled override).
    private bool MacEnabled() => File.Exists(LaunchAgentPath) && OwnsLaunchAgent(LaunchAgentPath) && MacDisabledByUser() != true;

    private static bool? MacDisabledByUser() => MacUserId() is { } user && Launchctl("print-disabled", $"gui/{user}") is { } output
        ? LaunchctlDisabled(output, LaunchAgentLabel) : null;

    private static string? MacUserId() => Run("/usr/bin/id", "-u")?.Trim() is { Length: > 0 } id && id.All(char.IsAsciiDigit) ? id : null;

    private static string? Launchctl(params string[] arguments) => Run("/bin/launchctl", arguments);

    private static string? Run(string program, params string[] arguments)
    {
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(5000) && process.ExitCode == 0 ? output : null;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { return null; }
    }

    /// <summary>Reads <c>launchctl print-disabled</c>: true when the label is disabled, false when enabled, null when absent.</summary>
    internal static bool? LaunchctlDisabled(string output, string label)
    {
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split("=>", 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Trim('"') != label) continue;
            return parts[1].TrimEnd(';', ',') switch { "disabled" or "true" => true, "enabled" or "false" => false, _ => null };
        }
        return null;
    }

    /// <summary>
    /// A login item must point at the app where it will still be after a restart. macOS runs apps opened
    /// straight from Downloads from a temporary translocated copy, and a mounted disk image disappears.
    /// </summary>
    internal static string? MacLaunchPathProblem(string shellPath) =>
        shellPath.Contains("/AppTranslocation/", StringComparison.Ordinal) || shellPath.StartsWith("/Volumes/", StringComparison.Ordinal)
            || shellPath.StartsWith("/private/var/folders/", StringComparison.Ordinal)
            ? "Move Mission Control to the Applications folder and open it from there, then turn on start at sign-in."
            : null;

    private void SetMacEnabled(bool enabled)
    {
        if (enabled && MacLaunchPathProblem(shellPath!) is { } problem) throw new InvalidOperationException(problem);
        var path = LaunchAgentPath;
        var directory = Path.GetDirectoryName(path)!;
        if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("The login item directory cannot be a symbolic link.");
        if (File.Exists(path) && ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || !OwnsLaunchAgent(path)))
            throw new InvalidOperationException("The existing Mission Control login item could not be verified and was preserved.");
        if (!enabled) { if (File.Exists(path)) File.Delete(path); return; }
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, LaunchAgentLabel + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var writer = XmlWriter.Create(temporary, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) }))
            {
                writer.WriteStartDocument();
                writer.WriteStartElement("plist"); writer.WriteAttributeString("version", "1.0");
                writer.WriteStartElement("dict");
                writer.WriteElementString("key", "Label"); writer.WriteElementString("string", LaunchAgentLabel);
                writer.WriteElementString("key", "ProgramArguments"); writer.WriteStartElement("array");
                foreach (var argument in new[] { shellPath!, "--background", "--data-dir", dataDirectory }) writer.WriteElementString("string", argument);
                writer.WriteEndElement();
                writer.WriteElementString("key", "RunAtLoad"); writer.WriteStartElement("true"); writer.WriteEndElement();
                writer.WriteEndElement(); writer.WriteEndElement(); writer.WriteEndDocument();
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        // Ticking the box is explicit consent, so clear an earlier "off" from System Settings. If macOS
        // still blocks the item, say where to allow it instead of showing it as on.
        if (MacUserId() is { } user) Launchctl("enable", $"gui/{user}/{LaunchAgentLabel}");
        if (MacDisabledByUser() == true)
            throw new InvalidOperationException("macOS has Mission Control switched off in System Settings > General > Login Items. Turn it on there.");
    }

    private bool OwnsLaunchAgent(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 65536) return false;
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var dictionary = XDocument.Load(reader).Root?.Element("dict");
            var label = dictionary?.Elements("key").FirstOrDefault(key => key.Value == "Label")?.NextNode as XElement;
            var arguments = dictionary?.Elements("key").FirstOrDefault(key => key.Value == "ProgramArguments")?.NextNode as XElement;
            return label?.Value == LaunchAgentLabel && arguments?.Name == "array"
                && OwnsArguments(arguments.Elements("string").Select(value => value.Value).ToArray(), dataDirectory, StringComparison.Ordinal);
        }
        catch (Exception error) when (error is IOException or XmlException or UnauthorizedAccessException) { return false; }
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
