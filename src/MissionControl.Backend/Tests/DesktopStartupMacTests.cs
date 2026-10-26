using MissionControl.Backend;
using Xunit;

namespace MissionControl.Backend.Tests;

/// <summary>macOS login item rules that can be checked on any platform.</summary>
public sealed class DesktopStartupMacTests
{
    private const string Label = "local.missioncontrol.desktop";

    [Theory]
    [InlineData("/Applications/Mission Control.app/Contents/MacOS/mission-control", false)]
    [InlineData("/Users/teammate/Applications/Mission Control.app/Contents/MacOS/mission-control", false)]
    [InlineData("/private/var/folders/x1/abc/T/AppTranslocation/0F2C/d/Mission Control.app/Contents/MacOS/mission-control", true)]
    [InlineData("/Volumes/Mission Control/Mission Control.app/Contents/MacOS/mission-control", true)]
    public void LoginItemRequiresAnAppLocationThatSurvivesARestart(string shellPath, bool refused)
    {
        var problem = DesktopStartup.MacLaunchPathProblem(shellPath);
        Assert.Equal(refused, problem is not null);
        if (refused) Assert.Contains("Applications", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("disabled services = {\n\t\"com.example.other\" => enabled\n\t\"local.missioncontrol.desktop\" => disabled\n}\n", true)]
    [InlineData("disabled services = {\n\t\"local.missioncontrol.desktop\" => enabled\n}\n", false)]
    [InlineData("{\n\t\"local.missioncontrol.desktop\" => true;\n}\n", true)]
    [InlineData("disabled services = {\n\t\"com.example.other\" => disabled\n}\n", null)]
    [InlineData("", null)]
    public void SwitchingTheItemOffInSystemSettingsIsRecognised(string output, bool? disabled) =>
        Assert.Equal(disabled, DesktopStartup.LaunchctlDisabled(output, Label));
}
