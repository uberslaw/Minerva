namespace CpuThrottle;

/// <summary>
/// Options for launching a process under a CPU-throttled Windows Job Object.
/// </summary>
public sealed class ThrottleOptions
{
    /// <summary>Hard CPU rate cap as a whole-number percent (1–100). Default 50.</summary>
    public int CpuPercent { get; init; } = 50;

    /// <summary>Optional limit on how many logical processors the job may use.</summary>
    public int? AffinityCoreCount { get; init; }

    /// <summary>Apply Windows 11 Efficiency Mode (EcoQoS) after launch.</summary>
    public bool EfficiencyMode { get; init; } = true;

    /// <summary>Process priority class applied after launch.</summary>
    public ThrottlePriority Priority { get; init; } = ThrottlePriority.BelowNormal;

    /// <summary>Working directory for the child process (null = inherit).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Validates and returns a normalized copy. Throws <see cref="ArgumentOutOfRangeException"/> on bad values.
    /// </summary>
    public ThrottleOptions Validate()
    {
        if (CpuPercent is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(CpuPercent), CpuPercent, "CPU percent must be between 1 and 100.");
        }

        if (AffinityCoreCount is < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(AffinityCoreCount), AffinityCoreCount, "Affinity core count must be at least 1.");
        }

        return this;
    }

    /// <summary>
    /// Job Object CpuRate field: percent × 100 (e.g. 50% → 5000).
    /// </summary>
    public uint ToJobCpuRate() => checked((uint)(Validate().CpuPercent * 100));
}

public enum ThrottlePriority
{
    Idle,
    BelowNormal,
    Normal,
}
