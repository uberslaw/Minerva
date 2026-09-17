using System.ComponentModel;
using CpuThrottle;

namespace CpuThrottle.Tests;

public class ProcessListSorterTests
{
    private static readonly RunningProcessEntry[] Sample =
    {
        new(10, "chrome", @"C:\Program Files\Google\Chrome\chrome.exe", 200_000_000, 12.5),
        new(20, "notepad", @"C:\Windows\System32\notepad.exe", 10_000_000, 0.1),
        new(5, "Chrome", @"C:\Program Files\Google\Chrome\chrome.exe", 150_000_000, 40.0),
        new(30, "solver", @"D:\Sim\solver.exe", 500_000_000, 88.2),
    };

    [Fact]
    public void Sort_ByName_Ascending_IsCaseInsensitive()
    {
        var sorted = ProcessListSorter.Sort(Sample, ProcessSortColumn.Name, ascending: true);
        Assert.Equal(new[] { 5, 10, 20, 30 }, sorted.Select(e => e.ProcessId));
    }

    [Fact]
    public void Sort_ByCpu_Descending()
    {
        var sorted = ProcessListSorter.Sort(Sample, ProcessSortColumn.CpuPercent, ascending: false);
        Assert.Equal(new[] { 30, 5, 10, 20 }, sorted.Select(e => e.ProcessId));
    }

    [Fact]
    public void Sort_ByWorkingSet_Ascending()
    {
        var sorted = ProcessListSorter.Sort(Sample, ProcessSortColumn.WorkingSet, ascending: true);
        Assert.Equal(new[] { 20, 5, 10, 30 }, sorted.Select(e => e.ProcessId));
    }

    [Fact]
    public void Sort_ByPath_ThenPidForTies()
    {
        var sorted = ProcessListSorter.Sort(Sample, ProcessSortColumn.Path, ascending: true);
        Assert.Equal(new[] { 5, 10, 20, 30 }, sorted.Select(e => e.ProcessId));
    }

    [Fact]
    public void Sort_ByProcessId_Descending()
    {
        var sorted = ProcessListSorter.Sort(Sample, ProcessSortColumn.ProcessId, ascending: false);
        Assert.Equal(new[] { 30, 20, 10, 5 }, sorted.Select(e => e.ProcessId));
    }
}

public class ProcessListFormatterTests
{
    [Theory]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2.0 KB")]
    [InlineData(5L * 1024 * 1024, "5.0 MB")]
    [InlineData(2L * 1024 * 1024 * 1024, "2.00 GB")]
    public void FormatWorkingSet_UsesReadableUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ProcessListFormatter.FormatWorkingSet(bytes));
    }

    [Fact]
    public void FormatCpuPercent_OneDecimal()
    {
        Assert.Equal("12.5%", ProcessListFormatter.FormatCpuPercent(12.5));
    }

    [Fact]
    public void DescribeAttachFailure_AccessDenied_IsClear()
    {
        var ex = new Win32Exception(5, "Access is denied.");
        var message = ProcessListFormatter.DescribeAttachFailure(ex, 4242);
        Assert.Contains("4242", message);
        Assert.Contains("access denied", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("job", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DescribeAttachFailure_GenericException_IncludesMessage()
    {
        var message = ProcessListFormatter.DescribeAttachFailure(new InvalidOperationException("boom"), 7);
        Assert.Contains("7", message);
        Assert.Contains("boom", message);
    }
}

public class ProcessListSamplerTests
{
    [Fact]
    public void Sample_ReturnsCurrentProcessEntry()
    {
        var sampler = new ProcessListSampler();
        var first = sampler.Sample();
        Assert.NotEmpty(first);

        // Second sample should include CPU estimates for processes that allow TotalProcessorTime.
        Thread.Sleep(50);
        var second = sampler.Sample();
        Assert.NotEmpty(second);
        Assert.Contains(second, e => e.ProcessId == Environment.ProcessId);
    }
}
