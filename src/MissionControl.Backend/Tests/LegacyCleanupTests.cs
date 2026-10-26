using System.Runtime.InteropServices;
using Xunit;

namespace MissionControl.Backend.Tests;

public sealed class LegacyCleanupTests
{
    [Theory]
    [InlineData("https://github.com/github/github-mcp-server", true)]
    [InlineData("https://github.com/github/github-mcp-server/blob/main/README.md", true)]
    [InlineData("https://github.com/github/github-mcp-server-evil", false)]
    [InlineData("https://code.claude.com/docs/en/mcp", true)]
    [InlineData("https://code.claude.com.evil.example/docs", false)]
    [InlineData("https://code.claude.com/docs?token=x", false)]
    [InlineData("file:///C:/Users/User/secrets.txt", false)]
    public void Documentation_links_are_strict(string url, bool allowed) => Assert.Equal(allowed, OfficialDocumentation.TryValidate(url, out _));

    [WindowsCredentialFact]
    public void Native_cleanup_deletes_only_synthetic_legacy_keys_in_its_own_scope()
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("WindowsCredentialFact must skip this test on other systems.");
        var prefix = Guid.NewGuid().ToString("N")[..24] + ":";
        var otherPrefix = Guid.NewGuid().ToString("N")[..24] + ":";
        var target = "MissionControl/" + prefix + "remote:synthetic-test:token";
        var unrelated = "MissionControl/" + otherPrefix + "remote:synthetic-test:token";
        try
        {
            Seed(target); Seed(unrelated);
            var cleaner = new WindowsLegacyCredentialStore();
            Assert.Throws<ArgumentException>(() => ((MissionControl.Core.ILegacyCredentialStore)cleaner).DeleteScope("*"));
            Assert.Throws<ArgumentException>(() => ((MissionControl.Core.ILegacyCredentialStore)cleaner).Delete("unscoped"));
            cleaner.DeleteScope(prefix);
            Assert.False(Exists(target)); Assert.True(Exists(unrelated));
            cleaner.Delete(prefix + "remote:synthetic-test:token"); // Idempotent missing deletion.
        }
        finally { CredDelete(target, 1, 0); CredDelete(unrelated, 1, 0); }
    }
    private static void Seed(string target)
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes("synthetic-disposable-value");
        var buffer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            var credential = new Credential { Type = 1, TargetName = target, BlobSize = (uint)bytes.Length, Blob = buffer, Persist = 2, UserName = "synthetic-test" };
            if (!CredWrite(ref credential, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static bool Exists(string target)
    { var found = CredRead(target, 1, 0, out var pointer); if (found) CredFree(pointer); return found; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type; public string? TargetName, Comment; public long LastWritten;
        public uint BlobSize; public IntPtr Blob; public uint Persist, AttributeCount; public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential value, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr value);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr value);
}
