namespace CpuThrottle;

/// <summary>
/// Snapshot of a running process for the attach / process-picker UI.
/// </summary>
public sealed class RunningProcessEntry
{
    public RunningProcessEntry(
        int processId,
        string name,
        string path,
        long workingSetBytes,
        double cpuPercent)
    {
        ProcessId = processId;
        Name = name ?? string.Empty;
        Path = path ?? string.Empty;
        WorkingSetBytes = workingSetBytes;
        CpuPercent = cpuPercent;
    }

    public int ProcessId { get; }
    public string Name { get; }
    public string Path { get; }
    public long WorkingSetBytes { get; }
    public double CpuPercent { get; }
}
