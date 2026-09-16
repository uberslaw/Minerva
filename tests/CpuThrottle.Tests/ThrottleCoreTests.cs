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
