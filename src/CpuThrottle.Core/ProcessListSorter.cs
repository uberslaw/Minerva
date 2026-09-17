namespace CpuThrottle;

public enum ProcessSortColumn
{
    Name,
    ProcessId,
    CpuPercent,
    WorkingSet,
    Path,
}

/// <summary>
/// Stable helpers for sorting process picker rows (shared by UI and tests).
/// </summary>
public static class ProcessListSorter
{
    public static IReadOnlyList<RunningProcessEntry> Sort(
        IEnumerable<RunningProcessEntry> entries,
        ProcessSortColumn column,
        bool ascending)
    {
        ArgumentNullException.ThrowIfNull(entries);

        IOrderedEnumerable<RunningProcessEntry> ordered = column switch
        {
            ProcessSortColumn.ProcessId => ascending
                ? entries.OrderBy(e => e.ProcessId)
                : entries.OrderByDescending(e => e.ProcessId),
            ProcessSortColumn.CpuPercent => ascending
                ? entries.OrderBy(e => e.CpuPercent)
                : entries.OrderByDescending(e => e.CpuPercent),
            ProcessSortColumn.WorkingSet => ascending
                ? entries.OrderBy(e => e.WorkingSetBytes)
                : entries.OrderByDescending(e => e.WorkingSetBytes),
            ProcessSortColumn.Path => ascending
                ? entries.OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                : entries.OrderByDescending(e => e.Path, StringComparer.OrdinalIgnoreCase),
            _ => ascending
                ? entries.OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                : entries.OrderByDescending(e => e.Name, StringComparer.OrdinalIgnoreCase),
        };

        // Secondary key keeps rows stable when primary values tie.
        return ordered.ThenBy(e => e.ProcessId).ToList();
    }
}
