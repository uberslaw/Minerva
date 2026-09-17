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

public class MinervaLogTests
{
    [Fact]
    public void Info_WritesToOverrideDirectory_WithoutThrowing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MinervaLogTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            MinervaLog.SetDirectoryOverride(dir);
            MinervaLog.EnsureStarted("unit-test");
            MinervaLog.Info("hello from test");
            MinervaLog.Warn("warn line");
            MinervaLog.Error("error line", new InvalidOperationException("boom"));

            var path = MinervaLog.LogFilePath;
            Assert.True(File.Exists(path), $"Expected log at {path}");
            var text = File.ReadAllText(path);
            Assert.Contains("Start unit-test", text, StringComparison.Ordinal);
            Assert.Contains("hello from test", text, StringComparison.Ordinal);
            Assert.Contains("warn line", text, StringComparison.Ordinal);
            Assert.Contains("boom", text, StringComparison.Ordinal);
        }
        finally
        {
            MinervaLog.SetDirectoryOverride(null);
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    [Fact]
    public void FormatOptions_IncludesKeyLimits()
    {
        var options = new ThrottleOptions
        {
            CpuPercent = 35,
            AffinityCoreCount = 4,
            EfficiencyMode = true,
            Priority = ThrottlePriority.BelowNormal,
            DiskReadBytesPerSecond = 10 * 1024 * 1024,
            NetworkTxBytesPerSecond = 5 * 1024 * 1024,
            GpuThrottle = GpuThrottleMode.Idle,
        };

        var text = MinervaLog.FormatOptions(options);
        Assert.Contains("cpu=35%", text, StringComparison.Ordinal);
        Assert.Contains("cores=4", text, StringComparison.Ordinal);
        Assert.Contains("ecoqos=on", text, StringComparison.Ordinal);
        Assert.Contains("gpu=Idle", text, StringComparison.Ordinal);
        Assert.Contains("diskRead=", text, StringComparison.Ordinal);
        Assert.Contains("netTx=", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Info_DoesNotThrow_WhenDirectoryUnwritable()
    {
        // Point at a path that cannot be created as a directory on most systems.
        MinervaLog.SetDirectoryOverride(
            OperatingSystem.IsWindows()
                ? "Z:\\Minerva\\definitely-missing-volume\\logs"
                : "/proc/minerva-log-should-fail");
        try
        {
            MinervaLog.EnsureStarted("unit-test-unwritable");
            MinervaLog.Info("should not throw");
            MinervaLog.Error("should not throw", new Exception("x"));
        }
        finally
        {
            MinervaLog.SetDirectoryOverride(null);
        }
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
