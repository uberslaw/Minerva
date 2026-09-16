namespace CpuThrottle;

/// <summary>
/// Options for launching a process under a resource-throttled Windows Job Object.
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

    /// <summary>
    /// Optional disk <em>read</em> bandwidth hint in bytes/second.
    /// Windows Job Objects expose a single combined I/O MaxBandwidth; see <see cref="EffectiveDiskBandwidthBytesPerSecond"/>.
    /// </summary>
    public long? DiskReadBytesPerSecond { get; init; }

    /// <summary>
    /// Optional disk <em>write</em> bandwidth hint in bytes/second.
    /// Windows Job Objects expose a single combined I/O MaxBandwidth; see <see cref="EffectiveDiskBandwidthBytesPerSecond"/>.
    /// </summary>
    public long? DiskWriteBytesPerSecond { get; init; }

    /// <summary>
    /// Optional network transmit (outbound) bandwidth cap in bytes/second via Job Object net rate control.
    /// Receive/inbound traffic is not limited by this API.
    /// </summary>
    public long? NetworkTxBytesPerSecond { get; init; }

    /// <summary>
    /// Best-effort GPU scheduling hint. There is no public Win32 hard GPU-% cap comparable to CPU rate control.
    /// </summary>
    public GpuThrottleMode GpuThrottle { get; init; } = GpuThrottleMode.Off;

    /// <summary>Working directory for the child process (null = inherit).</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>
    /// Combined disk I/O bandwidth applied to the job (bytes/sec), or null when neither disk limit is set.
    /// Read and write hints are summed when both are specified because the kernel enforces one MaxBandwidth pool.
    /// </summary>
    public long? EffectiveDiskBandwidthBytesPerSecond
    {
        get
        {
            long total = 0;
            var any = false;
            if (DiskReadBytesPerSecond is long read)
            {
                total = checked(total + read);
                any = true;
            }

            if (DiskWriteBytesPerSecond is long write)
            {
                total = checked(total + write);
                any = true;
            }

            return any ? total : null;
        }
    }

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

        ValidatePositiveRate(DiskReadBytesPerSecond, nameof(DiskReadBytesPerSecond));
        ValidatePositiveRate(DiskWriteBytesPerSecond, nameof(DiskWriteBytesPerSecond));
        ValidatePositiveRate(NetworkTxBytesPerSecond, nameof(NetworkTxBytesPerSecond));

        if (EffectiveDiskBandwidthBytesPerSecond is long combined && combined <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(EffectiveDiskBandwidthBytesPerSecond),
                combined,
                "Combined disk bandwidth must be positive when disk limits are set.");
        }

        if (!Enum.IsDefined(GpuThrottle))
        {
            throw new ArgumentOutOfRangeException(nameof(GpuThrottle), GpuThrottle, "Unknown GPU throttle mode.");
        }

        return this;
    }

    /// <summary>
    /// Job Object CpuRate field: percent × 100 (e.g. 50% → 5000).
    /// </summary>
    public uint ToJobCpuRate() => checked((uint)(Validate().CpuPercent * 100));

    private static void ValidatePositiveRate(long? rate, string name)
    {
        if (rate is < 1)
        {
            throw new ArgumentOutOfRangeException(name, rate, "Bandwidth limit must be at least 1 byte/second when set.");
        }
    }
}

public enum ThrottlePriority
{
    Idle,
    BelowNormal,
    Normal,
}

/// <summary>
/// GPU throttling is best-effort only: Windows does not expose a public hard GPU utilization cap for arbitrary processes.
/// </summary>
public enum GpuThrottleMode
{
    /// <summary>Do not change GPU scheduling priority.</summary>
    Off = 0,

    /// <summary>
    /// Request idle GPU scheduling priority via D3DKMT (WDDM). Soft hint — not a percent hard cap.
    /// </summary>
    LowPriority = 1,
}
