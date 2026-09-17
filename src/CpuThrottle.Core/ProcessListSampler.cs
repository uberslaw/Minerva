using System.Diagnostics;

namespace CpuThrottle;

/// <summary>
/// Enumerates running processes and estimates per-process CPU % between consecutive samples.
/// Call <see cref="Sample"/> periodically (e.g. every 1–2 s) from a background thread so the UI stays responsive.
/// </summary>
public sealed class ProcessListSampler
{
    private readonly Dictionary<int, TimeSpan> _lastProcessorTime = new();
    private DateTime _lastSampleUtc = DateTime.UtcNow;
    private readonly int _processorCount;

    public ProcessListSampler(int? processorCount = null)
    {
        _processorCount = processorCount is > 0
            ? processorCount.Value
            : Math.Max(1, Environment.ProcessorCount);
    }

    /// <summary>
    /// Captures a process list. CPU % is 0 on the first call (or for newly appeared PIDs)
    /// until a later sample provides an interval.
    /// </summary>
    public IReadOnlyList<RunningProcessEntry> Sample()
    {
        var now = DateTime.UtcNow;
        var wall = now - _lastSampleUtc;
        var nextTimes = new Dictionary<int, TimeSpan>();
        var results = new List<RunningProcessEntry>();

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return Array.Empty<RunningProcessEntry>();
        }

        foreach (var process in processes)
        {
            try
            {
                int pid;
                string name;
                try
                {
                    pid = process.Id;
                    name = process.ProcessName;
                }
                catch
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                long workingSet = 0;
                try
                {
                    workingSet = process.WorkingSet64;
                }
                catch
                {
                    // Access denied or process exited.
                }

                var path = TryGetPath(process);

                double cpuPercent = 0;
                try
                {
                    var total = process.TotalProcessorTime;
                    nextTimes[pid] = total;
                    if (wall > TimeSpan.Zero && _lastProcessorTime.TryGetValue(pid, out var previous))
                    {
                        var usedMs = (total - previous).TotalMilliseconds;
                        cpuPercent = usedMs / wall.TotalMilliseconds / _processorCount * 100.0;
                        cpuPercent = Math.Clamp(cpuPercent, 0, 100);
                    }
                }
                catch
                {
                    // Some system processes deny TotalProcessorTime.
                }

                results.Add(new RunningProcessEntry(pid, name, path, workingSet, cpuPercent));
            }
            finally
            {
                process.Dispose();
            }
        }

        _lastProcessorTime.Clear();
        foreach (var pair in nextTimes)
        {
            _lastProcessorTime[pair.Key] = pair.Value;
        }

        _lastSampleUtc = now;
        return results;
    }

    private static string TryGetPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch
        {
            // Access denied for elevated / protected processes is common.
            return string.Empty;
        }
    }
}
