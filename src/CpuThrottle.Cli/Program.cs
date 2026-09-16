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
    "Launch (or attach to) a process under a Windows Job Object with a hard CPU rate cap, " +
    "so heavy modelling workloads leave the desktop usable.")
{
    cpuOption,
    coresOption,
    efficiencyOption,
    noEfficiencyOption,
    priorityOption,
    waitOption,
    noWaitOption,
    attachOption,
    workingDirOption,
    exeArgument,
    argsArgument,
};

root.SetHandler(async (context) =>
{
    var cpu = context.ParseResult.GetValueForOption(cpuOption);
    var cores = context.ParseResult.GetValueForOption(coresOption);
    var efficiency = context.ParseResult.GetValueForOption(efficiencyOption);
    var noEfficiency = context.ParseResult.GetValueForOption(noEfficiencyOption);
    var priorityText = context.ParseResult.GetValueForOption(priorityOption) ?? "below-normal";
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
        Console.Error.WriteLine("CpuThrottle requires Windows 8+ (Job Object CPU rate control). This host is not Windows.");
        context.ExitCode = 2;
        return;
    }

    ThrottlePriority priority;
    try
    {
        priority = ParsePriority(priorityText);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine(ex.Message);
        context.ExitCode = 2;
        return;
    }

    var options = new ThrottleOptions
    {
        CpuPercent = cpu,
        AffinityCoreCount = cores,
        EfficiencyMode = efficiency,
        Priority = priority,
        WorkingDirectory = workingDir,
    };

    try
    {
        options.Validate();
    }
    catch (ArgumentOutOfRangeException ex)
    {
        Console.Error.WriteLine(ex.Message);
        context.ExitCode = 2;
        return;
    }

    if (attachPid is null && string.IsNullOrWhiteSpace(executable))
    {
        Console.Error.WriteLine("Provide an executable to launch, or --attach <pid>.");
        context.ExitCode = 2;
        return;
    }

    if (attachPid is not null && !string.IsNullOrWhiteSpace(executable))
    {
        Console.Error.WriteLine("Use either an executable or --attach, not both.");
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
        Console.WriteLine($"Attaching PID {pid} with CPU hard cap {options.CpuPercent}%...");
        process = ThrottledProcess.Attach(pid, options);
    }
    else
    {
        var argString = string.Join(' ', args.Select(QuoteIfNeeded));
        Console.WriteLine($"Launching '{executable}' with CPU hard cap {options.CpuPercent}%...");
        process = ThrottledProcess.Start(executable!, string.IsNullOrEmpty(argString) ? null : argString, options);
    }
#pragma warning restore CA1416

    using (process)
    {
        Console.WriteLine($"PID {process.ProcessId} is under job control (efficiency={(options.EfficiencyMode ? "on" : "off")}, priority={options.Priority}).");

        if (!wait)
        {
            Console.WriteLine("Detaching: keep this process alive to retain the job, or the job will close.");
            // Keep the job handle alive by parking until Ctrl+C when not waiting on child alone.
            var exit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                exit.Set();
            };
            exit.Wait();
            return 0;
        }

        using var monitor = new ProcessCpuMonitor(process.ProcessId);
        _ = monitor.SampleCpuPercent();

        while (!process.WaitForExit(1000))
        {
            var procCpu = monitor.SampleCpuPercent();
            Console.WriteLine($"  process CPU ~{procCpu:0.0}% of machine (cap {options.CpuPercent}%)");
        }

        var code = process.ExitCode;
        Console.WriteLine($"Process exited with code {code}.");
        return code;
    }
}

static ThrottlePriority ParsePriority(string value) => value.Trim().ToLowerInvariant() switch
{
    "idle" => ThrottlePriority.Idle,
    "below-normal" or "belownormal" or "below_normal" => ThrottlePriority.BelowNormal,
    "normal" => ThrottlePriority.Normal,
    _ => throw new ArgumentException($"Unknown priority '{value}'. Use idle, below-normal, or normal."),
};

static string QuoteIfNeeded(string value)
{
    if (value.Length == 0)
    {
        return "\"\"";
    }

    return value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0 ? $"\"{value.Replace("\"", "\\\"")}\"" : value;
}
