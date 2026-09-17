using CpuThrottle;

namespace CpuThrottle.Tests;

public class ThrottleOptionsTests
{
    [Theory]
    [InlineData(1, 100u)]
    [InlineData(50, 5000u)]
    [InlineData(100, 10000u)]
    public void ToJobCpuRate_MatchesWindowsConvention(int percent, uint expectedRate)
    {
        var options = new ThrottleOptions { CpuPercent = percent };
        Assert.Equal(expectedRate, options.ToJobCpuRate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Validate_RejectsInvalidCpuPercent(int percent)
    {
        var options = new ThrottleOptions { CpuPercent = percent };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_RejectsZeroAffinityCores()
    {
        var options = new ThrottleOptions { AffinityCoreCount = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_AllowsNullAffinity()
    {
        var options = new ThrottleOptions { CpuPercent = 40, AffinityCoreCount = null };
        Assert.Same(options, options.Validate());
    }

    [Fact]
    public void EffectiveDiskBandwidth_SumsReadAndWriteHints()
    {
        var options = new ThrottleOptions
        {
            DiskReadBytesPerSecond = 10_000,
            DiskWriteBytesPerSecond = 5_000,
        };
        Assert.Equal(15_000, options.EffectiveDiskBandwidthBytesPerSecond);
    }

    [Fact]
    public void EffectiveDiskBandwidth_NullWhenUnset()
    {
        var options = new ThrottleOptions();
        Assert.Null(options.EffectiveDiskBandwidthBytesPerSecond);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Validate_RejectsNonPositiveDiskRates(long rate)
    {
        var options = new ThrottleOptions { DiskReadBytesPerSecond = rate };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_RejectsNonPositiveNetworkTx()
    {
        var options = new ThrottleOptions { NetworkTxBytesPerSecond = 0 };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Fact]
    public void Validate_AllowsResourceLimits()
    {
        var options = new ThrottleOptions
        {
            CpuPercent = 25,
            DiskReadBytesPerSecond = 1024,
            DiskWriteBytesPerSecond = 2048,
            NetworkTxBytesPerSecond = 4096,
            GpuThrottle = GpuThrottleMode.Idle,
        };
        Assert.Same(options, options.Validate());
    }

    [Theory]
    [InlineData(GpuThrottleMode.Off)]
    [InlineData(GpuThrottleMode.Idle)]
    [InlineData(GpuThrottleMode.LowPriority)]
    [InlineData(GpuThrottleMode.BelowNormal)]
    [InlineData(GpuThrottleMode.Normal)]
    public void Validate_AllowsGpuThrottleModes(GpuThrottleMode mode)
    {
        var options = new ThrottleOptions { GpuThrottle = mode };
        Assert.Same(options, options.Validate());
    }

    [Fact]
    public void LowPriority_IsAliasForIdle()
    {
        Assert.Equal(GpuThrottleMode.Idle, GpuThrottleMode.LowPriority);
    }
}

public class BandwidthParserTests
{
    [Theory]
    [InlineData("1024", 1024L)]
    [InlineData("1K", 1024L)]
    [InlineData("10M", 10L * 1024 * 1024)]
    [InlineData("1.5M", (long)(1.5 * 1024 * 1024))]
    [InlineData("2G", 2L * 1024 * 1024 * 1024)]
    public void ParseBytesPerSecond_AcceptsSuffixes(string text, long expected)
    {
        Assert.Equal(expected, BandwidthParser.ParseBytesPerSecond(text));
    }

    [Fact]
    public void ParseBytesPerSecond_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(BandwidthParser.ParseBytesPerSecond(null));
        Assert.Null(BandwidthParser.ParseBytesPerSecond("  "));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-1M")]
    public void ParseBytesPerSecond_RejectsInvalid(string text)
    {
        Assert.ThrowsAny<Exception>(() => BandwidthParser.ParseBytesPerSecond(text));
    }

    [Fact]
    public void FormatBytesPerSecond_UsesSuffix()
    {
        Assert.Equal("10M", BandwidthParser.FormatBytesPerSecond(10L * 1024 * 1024));
        Assert.Equal("512K", BandwidthParser.FormatBytesPerSecond(512 * 1024));
    }
}

public class AffinityMaskTests
{
    [Fact]
    public void ForCoreCount_SetsLowestBits()
    {
        Assert.Equal((nuint)0b1, AffinityMask.ForCoreCount(1, 8));
        Assert.Equal((nuint)0b11, AffinityMask.ForCoreCount(2, 8));
        Assert.Equal((nuint)0b1111, AffinityMask.ForCoreCount(4, 8));
    }

    [Fact]
    public void ForCoreCount_ClampsToProcessorCount()
    {
        Assert.Equal((nuint)0b111, AffinityMask.ForCoreCount(16, 3));
    }

    [Fact]
    public void ForCoreCount_RejectsInvalidInputs()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AffinityMask.ForCoreCount(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => AffinityMask.ForCoreCount(2, 0));
    }
}

public class SampleBurnerProcessTreeTests
{
    [Fact]
    public async Task SampleBurner_CanSpawnChildProcessTree()
    {
        var repo = FindRepoRoot();
        var project = Path.Combine(repo, "samples", "CpuThrottle.SampleBurner", "CpuThrottle.SampleBurner.csproj");
        Assert.True(File.Exists(project), $"Missing sample project at {project}");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList =
            {
                "run",
                "--project", project,
                "-c", "Release",
                "--no-restore",
                "--",
                "--seconds", "2",
                "--threads", "2",
                "--children", "1",
                "--child-seconds", "2",
            },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = repo,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(process);
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        var output = await stdoutTask;
        var errors = await stderrTask;

        Assert.True(process.ExitCode == 0, $"Burner failed ({process.ExitCode}): {errors}\n{output}");
        Assert.Contains("Spawned child PID", output);
        Assert.Contains("Done.", output);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CpuThrottle.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate CpuThrottle.sln from test base directory.");
    }
}
