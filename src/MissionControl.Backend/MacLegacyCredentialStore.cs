using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using MissionControl.Core;

namespace MissionControl.Backend;

/// <summary>Upgrade-only deletion. Matching queries request item attributes, never secret data.</summary>
[SupportedOSPlatform("macos")]
internal sealed partial class MacLegacyCredentialStore : ILegacyCredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly IntPtr SecurityLibrary = NativeLibrary.Load(Security), FoundationLibrary = NativeLibrary.Load(Foundation);
    private static IntPtr Constant(string key) => Marshal.ReadIntPtr(NativeLibrary.GetExport(key.StartsWith("kCF", StringComparison.Ordinal) ? FoundationLibrary : SecurityLibrary, key));
    public void Delete(string key)
    {
        if (!KeyPattern().IsMatch(key)) throw new ArgumentException("Invalid retired credential reference.");
        using var query = new Query(); query.Text("kSecAttrAccount", key);
        var status = SecItemDelete(query.Handle);
        if (status is not (0 or -25300)) throw new InvalidOperationException("Old Keychain cleanup is unavailable and remains queued.");
    }
    public void DeleteScope(string prefix)
    {
        if (!PrefixPattern().IsMatch(prefix)) throw new ArgumentException("Invalid retired credential scope.");
        using var query = new Query();
        query.Set("kSecReturnAttributes", Constant("kCFBooleanTrue")); query.Set("kSecMatchLimit", Constant("kSecMatchLimitAll"));
        var status = SecItemCopyMatching(query.Handle, out var attributes);
        if (status == -25300) return;
        if (status != 0) throw new InvalidOperationException("Old Keychain cleanup is unavailable and remains queued.");
        try
        {
            var count = CFArrayGetCount(attributes);
            if (count > 10000) throw new InvalidOperationException("Old Keychain cleanup exceeded its bounded item count.");
            var buffer = new byte[256];
            for (nint i = 0; i < count; i++)
            {
                var account = CFDictionaryGetValue(CFArrayGetValueAtIndex(attributes, i), Constant("kSecAttrAccount"));
                if (account == IntPtr.Zero || !CFStringGetCString(account, buffer, buffer.Length, 0x08000100)) continue;
                var key = Encoding.UTF8.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0));
                if (key.StartsWith(prefix, StringComparison.Ordinal) && KeyPattern().IsMatch(key)) Delete(key);
            }
        }
        finally { if (attributes != IntPtr.Zero) CFRelease(attributes); }
    }
    [GeneratedRegex("^[a-f0-9]{24}:$", RegexOptions.CultureInvariant)] private static partial Regex PrefixPattern();
    [GeneratedRegex("^[a-f0-9]{24}:(?:remote-api-key:[A-F0-9]{64}|remote:[A-Za-z0-9._-]{1,64}:(?:token|socket))$", RegexOptions.CultureInvariant)] private static partial Regex KeyPattern();
    private sealed class Query : IDisposable
    {
        public IntPtr Handle { get; } = CFDictionaryCreateMutable(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        private readonly List<IntPtr> strings = [];
        public Query() { Set("kSecClass", Constant("kSecClassGenericPassword")); Text("kSecAttrService", "MissionControl"); }
        public void Set(string key, IntPtr value) => CFDictionarySetValue(Handle, Constant(key), value);
        public void Text(string key, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var text = CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, 0x08000100, false);
            if (text == IntPtr.Zero) throw new InvalidOperationException("Unable to prepare old Keychain cleanup.");
            strings.Add(text); Set(key, text);
        }
        public void Dispose() { CFRelease(Handle); foreach (var value in strings) CFRelease(value); }
    }
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Foundation)] private static extern nint CFArrayGetCount(IntPtr array);
    [DllImport(Foundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, nint index);
    [DllImport(Foundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
    [DllImport(Foundation)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keyCallbacks, IntPtr valueCallbacks);
    [DllImport(Foundation)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(Foundation)] private static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint length, uint encoding, [MarshalAs(UnmanagedType.I1)] bool external);
    [DllImport(Foundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr value, byte[] buffer, nint length, uint encoding);
    [DllImport(Foundation)] private static extern void CFRelease(IntPtr value);
}
