using System.CommandLine;
using System.Runtime.Versioning;
using CpuThrottle;

var cpuOption = new Option<int>(
    name: "--cpu",
    description: "Hard CPU rate cap as a percent of all processors (1-100).",
    getDefaultValue: () => 50);
cpuOption.AddAlias("-c");

var coresOption = new Option<int?>(
    name: "--cores",
    description: "Optional affinity: limit the job to the first N logical processors.");
coresOption.AddAlias("-n");

var efficiencyOption = new Option<bool>(
    name: "--efficiency",
    description: "Enable Windows 11 Efficiency Mode (EcoQoS).",
    getDefaultValue: () => true);
efficiencyOption.AddAlias("-e");

var noEfficiencyOption = new Option<bool>(
    name: "--no-efficiency",
    description: "Disable Efficiency Mode / EcoQoS.");

var priorityOption = new Option<string>(
    name: "--priority",
    description: "Process priority: idle | below-normal | normal.",
    getDefaultValue: () => "below-normal");
priorityOption.AddAlias("-p");

var diskReadOption = new Option<string?>(
    name: "--disk-read",
    description: "Disk read bandwidth hint in bytes/sec (K/M/G suffixes ok). Combined with --disk-write into one Job Object MaxBandwidth pool.");
diskReadOption.AddAlias("-dr");

var diskWriteOption = new Option<string?>(
    name: "--disk-write",
    description: "Disk write bandwidth hint in bytes/sec (K/M/G suffixes ok). Combined with --disk-read into one Job Object MaxBandwidth pool.");
diskWriteOption.AddAlias("-dw");

var networkTxOption = new Option<string?>(
    name: "--network-tx",
    description: "Network transmit (outbound) bandwidth cap in bytes/sec (K/M/G suffixes ok). Job Object net rate control; inbound is not limited.");
networkTxOption.AddAlias("-nt");

var gpuOption = new Option<string>(
    name: "--gpu",
    description: "Best-effort WDDM GPU scheduling hint (not a hard GPU % cap): off | idle|low | below-normal | normal.",
    getDefaultValue: () => "off");
gpuOption.AddAlias("-g");

var waitOption = new Option<bool>(
    name: "--wait",
    description: "Wait for the child process to exit and forward its exit code.",
    getDefaultValue: () => true);

var noWaitOption = new Option<bool>(
    name: "--no-wait",
    description: "Launch and return immediately (job stays open until this process exits).");

var attachOption = new Option<int?>(
    name: "--attach",
    description: "Attach to an already-running process ID instead of launching.");
attachOption.AddAlias("-a");

var workingDirOption = new Option<string?>(
    name: "--working-directory",
    description: "Working directory for the launched process.");
workingDirOption.AddAlias("-w");

var exeArgument = new Argument<string?>("executable", "Path to the modelling application executable.");
var argsArgument = new Argument<string[]>("args", "Arguments passed to the executable.")
{
    Arity = ArgumentArity.ZeroOrMore,
};

var root = new RootCommand(
    "Launch (or attach to) a process under a Windows Job Object with CPU, optional disk I/O, " +
    "and optional network Tx rate limits, plus best-effort GPU scheduling hints.")
{
    cpuOption,
    coresOption,
    efficiencyOption,
    noEfficiencyOption,
    priorityOption,
    diskReadOption,
    diskWriteOption,
    networkTxOption,
    gpuOption,
    waitOption,
    noWaitOption,
    attachOption,
    workingDirOption,
    exeArgument,
    argsArgument,
};

root.SetHandler(async (context) =>
{
    MinervaLog.EnsureStarted("CpuThrottle.Cli");

    var cpu = context.ParseResult.GetValueForOption(cpuOption);
    var cores = context.ParseResult.GetValueForOption(coresOption);
    var efficiency = context.ParseResult.GetValueForOption(efficiencyOption);
    var noEfficiency = context.ParseResult.GetValueForOption(noEfficiencyOption);
    var priorityText = context.ParseResult.GetValueForOption(priorityOption) ?? "below-normal";
    var diskReadText = context.ParseResult.GetValueForOption(diskReadOption);
    var diskWriteText = context.ParseResult.GetValueForOption(diskWriteOption);
    var networkTxText = context.ParseResult.GetValueForOption(networkTxOption);
    var gpuText = context.ParseResult.GetValueForOption(gpuOption) ?? "off";
    var wait = context.ParseResult.GetValueForOption(waitOption);
    var noWait = context.ParseResult.GetValueForOption(noWaitOption);
    var attachPid = context.ParseResult.GetValueForOption(attachOption);
    var workingDir = context.ParseResult.GetValueForOption(workingDirOption);
    var executable = context.ParseResult.GetValueForArgument(exeArgument);
    var args = context.ParseResult.GetValueForArgument(argsArgument) ?? Array.Empty<string>();

    if (noEfficiency)
    {
        efficiency = false;
    }

    if (noWait)
    {
        wait = false;
    }

    if (!OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("CpuThrottle requires Windows 8+ (Job Object rate control). This host is not Windows.");
        MinervaLog.Error("Refusing to run: host is not Windows.");
        context.ExitCode = 2;
        return;
    }

    ThrottlePriority priority;
    GpuThrottleMode gpu;
    long? diskRead;
    long? diskWrite;
    long? networkTx;
    try
    {
        priority = ParsePriority(priorityText);
        gpu = ParseGpu(gpuText);
        diskRead = BandwidthParser.ParseBytesPerSecond(diskReadText);
        diskWrite = BandwidthParser.ParseBytesPerSecond(diskWriteText);
        networkTx = BandwidthParser.ParseBytesPerSecond(networkTxText);
    }
    catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
    {
        Console.Error.WriteLine(ex.Message);
        MinervaLog.Error("Invalid CLI options", ex);
        context.ExitCode = 2;
        return;
    }

    var options = new ThrottleOptions
    {
        CpuPercent = cpu,
        AffinityCoreCount = cores,
        EfficiencyMode = efficiency,
        Priority = priority,
        DiskReadBytesPerSecond = diskRead,
        DiskWriteBytesPerSecond = diskWrite,
        NetworkTxBytesPerSecond = networkTx,
        GpuThrottle = gpu,
        WorkingDirectory = workingDir,
    };

    try
    {
        options.Validate();
    }
    catch (ArgumentOutOfRangeException ex)
    {
        Console.Error.WriteLine(ex.Message);
        MinervaLog.Error("ThrottleOptions validation failed", ex);
        context.ExitCode = 2;
        return;
    }

    if (attachPid is null && string.IsNullOrWhiteSpace(executable))
    {
        Console.Error.WriteLine("Provide an executable to launch, or --attach <pid>.");
        MinervaLog.Warn("No executable or --attach provided.");
        context.ExitCode = 2;
        return;
    }

    if (attachPid is not null && !string.IsNullOrWhiteSpace(executable))
    {
        Console.Error.WriteLine("Use either an executable or --attach, not both.");
        MinervaLog.Warn("Both executable and --attach provided.");
        context.ExitCode = 2;
        return;
    }

    try
    {
        if (OperatingSystem.IsWindows())
        {
            context.ExitCode = RunWindows(attachPid, executable, args, options, wait);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
        MinervaLog.Error("CLI run failed", ex);
        context.ExitCode = 1;
    }

    await Task.CompletedTask;
});

return await root.InvokeAsync(args);

[SupportedOSPlatform("windows")]
#pragma warning disable CA1416 // Guarded by OperatingSystem.IsWindows() at call site
static int RunWindows(int? attachPid, string? executable, string[] args, ThrottleOptions options, bool wait)
{
    ThrottledProcess process;
    if (attachPid is int pid)
    {
        Console.WriteLine($"Attaching PID {pid} with {DescribeLimits(options)}...");
        MinervaLog.Info($"CLI attach requested pid={pid} | {MinervaLog.FormatOptions(options)}");
        process = ThrottledProcess.Attach(pid, options);
    }
    else
    {
        var argString = string.Join(' ', args.Select(QuoteIfNeeded));
        Console.WriteLine($"Launching '{executable}' with {DescribeLimits(options)}...");
        MinervaLog.Info($"CLI launch requested path='{executable}' args='{argString}' | {MinervaLog.FormatOptions(options)}");
        process = ThrottledProcess.Start(executable!, string.IsNullOrEmpty(argString) ? null : argString, options);
    }
#pragma warning restore CA1416

    using (process)
    {
        Console.WriteLine(
            $"PID {process.ProcessId} is under job control " +
            $"(efficiency={(options.EfficiencyMode ? "on" : "off")}, priority={options.Priority}, gpu={options.GpuThrottle}).");
        Console.WriteLine($"Diagnostic log: {MinervaLog.LogFilePath}");

        if (!wait)
        {
            Console.WriteLine("Detaching: keep this process alive to retain the job, or the job will close.");
            MinervaLog.Info($"CLI --no-wait: holding job open for pid={process.ProcessId} until Ctrl+C");
            // Keep the job handle alive by parking until Ctrl+C when not waiting on child alone.
            var exit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                MinervaLog.Info("CLI Ctrl+C — releasing job");
                exit.Set();
            };
            exit.Wait();
            return 0;
        }

        using var monitor = new ProcessCpuMonitor(process.ProcessId);
        _ = monitor.SampleCpuPercent();
        var sampleTicks = 0;

        while (!process.WaitForExit(1000))
        {
            var procCpu = monitor.SampleCpuPercent();
            Console.WriteLine($"  process CPU ~{procCpu:0.0}% of machine (cap {options.CpuPercent}%)");
            sampleTicks++;
            // Log roughly every 10 seconds to avoid spam.
            if (sampleTicks % 10 == 0)
            {
                MinervaLog.Info($"CPU sample pid={process.ProcessId} ~{procCpu:0.0}% (cap {options.CpuPercent}%)");
            }
        }

        var code = process.ExitCode;
        Console.WriteLine($"Process exited with code {code}.");
        MinervaLog.Info($"CLI child exited pid={process.ProcessId} code={code}");
        return code;
    }
}

static string DescribeLimits(ThrottleOptions options)
{
    var parts = new List<string> { $"CPU hard cap {options.CpuPercent}%" };
    if (options.DiskReadBytesPerSecond is long read)
    {
        parts.Add($"disk-read hint {BandwidthParser.FormatBytesPerSecond(read)}B/s");
    }

    if (options.DiskWriteBytesPerSecond is long write)
    {
        parts.Add($"disk-write hint {BandwidthParser.FormatBytesPerSecond(write)}B/s");
    }

    if (options.EffectiveDiskBandwidthBytesPerSecond is long combined
        && (options.DiskReadBytesPerSecond is not null || options.DiskWriteBytesPerSecond is not null))
    {
        parts.Add($"disk combined MaxBandwidth {BandwidthParser.FormatBytesPerSecond(combined)}B/s");
    }

    if (options.NetworkTxBytesPerSecond is long tx)
    {
        parts.Add($"network Tx {BandwidthParser.FormatBytesPerSecond(tx)}B/s");
    }

    return string.Join(", ", parts);
}

static ThrottlePriority ParsePriority(string value) => value.Trim().ToLowerInvariant() switch
{
    "idle" => ThrottlePriority.Idle,
    "below-normal" or "belownormal" or "below_normal" => ThrottlePriority.BelowNormal,
    "normal" => ThrottlePriority.Normal,
    _ => throw new ArgumentException($"Unknown priority '{value}'. Use idle, below-normal, or normal."),
};

static GpuThrottleMode ParseGpu(string value) => value.Trim().ToLowerInvariant() switch
{
    "off" or "none" or "false" or "0" => GpuThrottleMode.Off,
    "low" or "low-priority" or "lowpriority" or "idle" or "true" or "1" => GpuThrottleMode.Idle,
    "below-normal" or "belownormal" or "below_normal" or "2" => GpuThrottleMode.BelowNormal,
    "normal" or "3" => GpuThrottleMode.Normal,
    _ => throw new ArgumentException(
        $"Unknown GPU mode '{value}'. Use off, idle (or low), below-normal, or normal."),
};

static string QuoteIfNeeded(string value)
{
    if (value.Length == 0)
    {
        return "\"\"";
    }

    return value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
