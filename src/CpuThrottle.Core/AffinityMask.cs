namespace CpuThrottle;

/// <summary>
/// Builds a process affinity bitmask for the first N logical processors.
/// </summary>
public static class AffinityMask
{
    /// <summary>
    /// Returns a mask with the lowest <paramref name="coreCount"/> bits set,
    /// clamped to <paramref name="processorCount"/> available processors.
    /// </summary>
    public static nuint ForCoreCount(int coreCount, int processorCount)
    {
        if (coreCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(coreCount), coreCount, "Core count must be at least 1.");
        }

        if (processorCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(processorCount), processorCount, "Processor count must be at least 1.");
        }

        var bits = Math.Min(coreCount, processorCount);
        // Cap at pointer width so we do not overflow nuint on atypical hosts.
        bits = Math.Min(bits, IntPtr.Size * 8);

        if (bits == IntPtr.Size * 8)
        {
            return nuint.MaxValue;
        }

        return (nuint)((1UL << bits) - 1UL);
    }
}
