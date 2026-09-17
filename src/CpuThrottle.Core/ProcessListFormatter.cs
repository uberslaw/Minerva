using System.ComponentModel;

namespace CpuThrottle;

/// <summary>
/// Display formatting for process picker columns.
/// </summary>
public static class ProcessListFormatter
{
    public static string FormatCpuPercent(double percent) => $"{percent:0.0}%";

    public static string FormatWorkingSet(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        const double kb = 1024.0;
        const double mb = kb * 1024.0;
        const double gb = mb * 1024.0;

        if (bytes >= gb)
        {
            return $"{bytes / gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0.0} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0.0} KB";
        }

        return $"{bytes} B";
    }

    /// <summary>
    /// Maps attach failures (access denied, already in a job, etc.) to a short user-facing message.
    /// </summary>
    public static string DescribeAttachFailure(Exception ex, int processId)
    {
        ArgumentNullException.ThrowIfNull(ex);

        if (ex is Win32Exception win32)
        {
            // ERROR_ACCESS_DENIED = 5; ERROR_INVALID_PARAMETER = 87
            if (win32.NativeErrorCode is 5 or 87)
            {
                return
                    $"Could not attach to PID {processId}: access denied or the process already belongs to another job. " +
                    "Try running CpuThrottle elevated, or pick a process that is not already job-constrained.";
            }

            return $"Could not attach to PID {processId}: {win32.Message} (Win32 {win32.NativeErrorCode}).";
        }

        return $"Could not attach to PID {processId}: {ex.Message}";
    }
}
