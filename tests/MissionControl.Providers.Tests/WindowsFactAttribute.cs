using Xunit;
namespace MissionControl.Providers.Tests;
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute() { if (!OperatingSystem.IsWindows()) Skip = "Windows file sharing semantics; Unix does not provide this guarantee."; }
}
