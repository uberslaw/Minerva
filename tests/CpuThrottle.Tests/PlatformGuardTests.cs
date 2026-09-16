using CpuThrottle;

namespace CpuThrottle.Tests;

public class PlatformGuardTests
{
    [Fact]
    public void Start_ThrowsOnNonWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

#pragma warning disable CA1416
        Assert.Throws<PlatformNotSupportedException>(() =>
            ThrottledProcess.Start("/bin/true", null, new ThrottleOptions { CpuPercent = 50 }));
#pragma warning restore CA1416
    }
}
