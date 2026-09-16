using System.Globalization;
using System.Text.RegularExpressions;

namespace CpuThrottle;

/// <summary>
/// Parses human-friendly bandwidth strings (e.g. 10M, 512K, 1048576) into bytes/second.
/// </summary>
public static class BandwidthParser
{
    private static readonly Regex Pattern = new(
        @"^\s*(?<value>\d+(\.\d+)?)\s*(?<suffix>[KkMmGgTt]i?[Bb]?)?\s*$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parses a rate string into bytes per second. Empty/null returns null.
    /// Suffixes: K/KB/KiB (1024), M/MB/MiB, G/GB/GiB, T/TB/TiB. Bare numbers are bytes/sec.
    /// </summary>
    public static long? ParseBytesPerSecond(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = Pattern.Match(text);
        if (!match.Success)
        {
            throw new FormatException(
                $"Invalid bandwidth '{text}'. Use a number with optional K/M/G/T suffix (e.g. 10M, 512K).");
        }

        var value = double.Parse(match.Groups["value"].Value, CultureInfo.InvariantCulture);
        if (value <= 0)
        {
            throw new FormatException($"Bandwidth must be positive (got '{text}').");
        }

        var suffix = match.Groups["suffix"].Value;
        var multiplier = SuffixMultiplier(suffix);
        var bytes = value * multiplier;
        if (bytes > long.MaxValue || double.IsInfinity(bytes) || double.IsNaN(bytes))
        {
            throw new OverflowException($"Bandwidth '{text}' is too large.");
        }

        var rounded = (long)Math.Round(bytes, MidpointRounding.AwayFromZero);
        if (rounded < 1)
        {
            throw new FormatException($"Bandwidth '{text}' rounds to less than 1 byte/second.");
        }

        return rounded;
    }

    /// <summary>Formats bytes/sec for display (e.g. 10485760 → "10M").</summary>
    public static string FormatBytesPerSecond(long bytesPerSecond)
    {
        if (bytesPerSecond < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytesPerSecond));
        }

        string[] units = ["", "K", "M", "G", "T"];
        double value = bytesPerSecond;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? bytesPerSecond.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{value:0.##}{units[unit]}");
    }

    private static double SuffixMultiplier(string suffix)
    {
        if (string.IsNullOrEmpty(suffix))
        {
            return 1;
        }

        var c = char.ToUpperInvariant(suffix[0]);
        return c switch
        {
            'K' => 1024d,
            'M' => 1024d * 1024,
            'G' => 1024d * 1024 * 1024,
            'T' => 1024d * 1024 * 1024 * 1024,
            'B' => 1,
            _ => throw new FormatException($"Unknown bandwidth suffix '{suffix}'."),
        };
    }
}
