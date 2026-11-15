using System.Runtime.Versioning;
using System.Security.Principal;

namespace SabhaMujic.Windows.Tests;

[SupportedOSPlatform("windows")]
public class WindowsHostSmokeTests
{
    [Fact]
    public void Runs_on_windows_with_a_resolvable_identity()
    {
        Assert.True(OperatingSystem.IsWindows());
        using var identity = WindowsIdentity.GetCurrent();
        Assert.NotNull(identity.User);
    }
}
