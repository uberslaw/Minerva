using System.Diagnostics;

namespace CpuThrottle;

/// <summary>
/// Samples process CPU usage as a percentage of total machine capacity.
/// </summary>
public sealed class ProcessCpuMonitor : IDisposable
{
    private readonly Process _process;
    private TimeSpan _lastProcessorTime;
    private DateTime _lastSampleUtc;
    private bool _disposed;

    public ProcessCpuMonitor(int processId)
    {
        _process = Process.GetProcessById(processId);
        _lastProcessorTime = _process.TotalProcessorTime;
        _lastSampleUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Returns approximate CPU usage of the target process as 0–100% of all logical processors.
    /// Call periodically (e.g. every 500–1000 ms).
    /// </summary>
    public double SampleCpuPercent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _process.Refresh();
        var now = DateTime.UtcNow;
        var cpu = _process.TotalProcessorTime;
        var wall = now - _lastSampleUtc;
        if (wall <= TimeSpan.Zero)
        {
            return 0;
        }

        var used = (cpu - _lastProcessorTime).TotalMilliseconds;
        var percent = used / wall.TotalMilliseconds / Environment.ProcessorCount * 100.0;

        _lastProcessorTime = cpu;
        _lastSampleUtc = now;
        return Math.Clamp(percent, 0, 100);
    }

    public static double SampleSystemCpuPercent()
    {
        // Use PerformanceCounter on Windows; fall back to 0 when unavailable (UI can still show process %).
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return SampleSystemCpuPercentWindows();
            }
        }
        catch
        {
            // Performance counters may be unavailable.
        }

        return 0;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static double SampleSystemCpuPercentWindows()
    {
        using var cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total");
        _ = cpu.NextValue();
        Thread.Sleep(100);
        return Math.Clamp(cpu.NextValue(), 0, 100);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _process.Dispose();
    }
}
