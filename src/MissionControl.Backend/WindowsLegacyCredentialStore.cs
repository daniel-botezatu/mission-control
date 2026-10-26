using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using MissionControl.Core;

namespace MissionControl.Backend;

/// <summary>Upgrade-only deletion of old app-owned credentials. Secret values are never read.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsLegacyCredentialStore : ILegacyCredentialStore
{
    public void Delete(string key)
    {
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException("Invalid retired credential reference.");
        if (!CredDelete("MissionControl/" + key, 1, 0) && Marshal.GetLastWin32Error() is var code && code != 1168)
            throw new Win32Exception(code, "The old Mission Control credential could not be removed. Cleanup remains queued.");
    }
    public void DeleteScope(string prefix)
    {
        if (!PrefixPattern().IsMatch(prefix)) throw new ArgumentException("Invalid retired credential scope.");
        var targetPrefix = "MissionControl/" + prefix;
        if (!CredEnumerate(targetPrefix + "*", 0, out var count, out var credentials))
        { var code = Marshal.GetLastWin32Error(); if (code == 1168) return; throw new Win32Exception(code, "Old credential cleanup is unavailable and remains queued."); }
        try
        {
            if (count > 10000) throw new InvalidOperationException("Old credential cleanup exceeded its bounded item count.");
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.PtrToStructure<Credential>(Marshal.ReadIntPtr(credentials, index * IntPtr.Size));
                if (item.Type == 1 && item.TargetName is { } target && target.StartsWith(targetPrefix, StringComparison.Ordinal) && KeyPattern().IsMatch(target[15..])) Delete(target[15..]);
            }
        }
        finally { CredFree(credentials); }
    }
    [GeneratedRegex("^[a-f0-9]{24}:$", RegexOptions.CultureInvariant)] private static partial Regex PrefixPattern();
    [GeneratedRegex("^[a-f0-9]{24}:(?:remote-api-key:[A-F0-9]{64}|remote:[A-Za-z0-9._-]{1,64}:(?:token|socket))$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type; public string? TargetName, Comment; public long LastWritten;
        public uint BlobSize; public IntPtr Blob; public uint Persist, AttributeCount; public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredEnumerate(string filter, uint flags, out int count, out IntPtr credentials);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credentials);
}
