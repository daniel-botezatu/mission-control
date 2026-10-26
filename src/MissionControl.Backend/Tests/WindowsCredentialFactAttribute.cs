using Xunit;
namespace MissionControl.Backend.Tests;
public sealed class WindowsCredentialFactAttribute : FactAttribute
{
    public WindowsCredentialFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows Credential Manager test; does not verify macOS Keychain."; }
}
