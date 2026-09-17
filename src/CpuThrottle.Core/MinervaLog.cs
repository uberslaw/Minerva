using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace CpuThrottle;

/// <summary>
/// Best-effort diagnostic file logger under %APPDATA%\Minerva (or the platform ApplicationData equivalent).
/// Never throws to callers — logging failures are swallowed so throttling keeps working.
/// </summary>
public static class MinervaLog
{
    private const long MaxLogBytes = 5 * 1024 * 1024; // 5 MiB before simple roll
    private static readonly object Gate = new();
    private static bool _started;
    private static string? _directoryOverride;

    /// <summary>Directory used for log files (created on first write).</summary>
    public static string LogDirectory
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_directoryOverride))
            {
                return _directoryOverride;
            }

            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrWhiteSpace(appData))
            {
                appData = Path.GetTempPath();
            }

            return Path.Combine(appData, "Minerva");
        }
    }

    /// <summary>Primary append log path (<c>Minerva.log</c>).</summary>
    public static string LogFilePath => Path.Combine(LogDirectory, "Minerva.log");

    /// <summary>Test hook: redirect log directory. Pass null to restore default.</summary>
    public static void SetDirectoryOverride(string? directory)
    {
        lock (Gate)
        {
            _directoryOverride = string.IsNullOrWhiteSpace(directory) ? null : directory;
            _started = false;
        }
    }

    /// <summary>
    /// Writes a one-time startup banner (version, PID, app name). Safe to call multiple times;
    /// only the first call per process (or after override change) emits the banner.
    /// </summary>
    public static void EnsureStarted(string appName)
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            WriteUnlocked(
                "INFO",
                $"Start {appName} | version={GetVersion()} | pid={Environment.ProcessId} | os={Environment.OSVersion} | processors={Environment.ProcessorCount}");
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? exception = null)
    {
        if (exception is null)
        {
            Write("ERROR", message);
            return;
        }

        var detail = exception is System.ComponentModel.Win32Exception win32
            ? $"{exception.GetType().Name}: {exception.Message} (Win32 {win32.NativeErrorCode})"
            : $"{exception.GetType().Name}: {exception.Message}";
        Write("ERROR", $"{message} | {detail}");
    }

    /// <summary>Formats throttle options for launch/attach log lines.</summary>
    public static string FormatOptions(ThrottleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var parts = new List<string>
        {
            $"cpu={options.CpuPercent}%",
            $"cores={(options.AffinityCoreCount?.ToString(CultureInfo.InvariantCulture) ?? "all")}",
            $"priority={options.Priority}",
            $"ecoqos={(options.EfficiencyMode ? "on" : "off")}",
            $"gpu={options.GpuThrottle}",
        };

        if (options.DiskReadBytesPerSecond is long read)
        {
            parts.Add($"diskRead={BandwidthParser.FormatBytesPerSecond(read)}B/s");
        }

        if (options.DiskWriteBytesPerSecond is long write)
        {
            parts.Add($"diskWrite={BandwidthParser.FormatBytesPerSecond(write)}B/s");
        }

        if (options.EffectiveDiskBandwidthBytesPerSecond is long combined
            && (options.DiskReadBytesPerSecond is not null || options.DiskWriteBytesPerSecond is not null))
        {
            parts.Add($"diskCombined={BandwidthParser.FormatBytesPerSecond(combined)}B/s");
        }

        if (options.NetworkTxBytesPerSecond is long tx)
        {
            parts.Add($"netTx={BandwidthParser.FormatBytesPerSecond(tx)}B/s");
        }

        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            parts.Add($"cwd={options.WorkingDirectory}");
        }

        return string.Join(", ", parts);
    }

    private static void Write(string level, string message)
    {
        lock (Gate)
        {
            WriteUnlocked(level, message);
        }
    }

    private static void WriteUnlocked(string level, string message)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            MaybeRollUnlocked();

            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");

            File.AppendAllText(LogFilePath, line, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            // Never fail the app because of logging.
            Debug.WriteLine($"MinervaLog write failed: {ex.Message}");
        }
    }

    private static void MaybeRollUnlocked()
    {
        try
        {
            if (!File.Exists(LogFilePath))
            {
                return;
            }

            var info = new FileInfo(LogFilePath);
            if (info.Length < MaxLogBytes)
            {
                return;
            }

            var backup = Path.Combine(LogDirectory, "Minerva.log.1");
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(LogFilePath, backup);
        }
        catch
        {
            // Ignore roll failures; append may still work or fail softly.
        }
    }

    private static string GetVersion()
    {
        try
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(MinervaLog).Assembly;
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                return informational;
            }

            var version = assembly.GetName().Version;
            return version?.ToString() ?? "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
