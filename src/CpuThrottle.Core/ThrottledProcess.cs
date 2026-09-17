using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace CpuThrottle;

/// <summary>
/// A child process running inside a Windows Job Object with CPU (and optional disk / network Tx) rate limits.
/// Dispose closes the job (and kills members when KillOnJobClose is set).
/// </summary>
public sealed class ThrottledProcess : IDisposable
{
    private readonly IntPtr _jobHandle;
    private readonly IntPtr _processHandle;
    private readonly IntPtr _threadHandle;
    private bool _disposed;

    private ThrottledProcess(
        IntPtr jobHandle,
        IntPtr processHandle,
        IntPtr threadHandle,
        int processId,
        ThrottleOptions options)
    {
        _jobHandle = jobHandle;
        _processHandle = processHandle;
        _threadHandle = threadHandle;
        ProcessId = processId;
        Options = options;
    }

    public int ProcessId { get; }
    public ThrottleOptions Options { get; }
    public bool HasExited => GetExitCode(out var code) && code != 259; /* STILL_ACTIVE */

    public int ExitCode
    {
        get
        {
            if (!GetExitCode(out var code))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (code == 259)
            {
                throw new InvalidOperationException("The process has not exited.");
            }

            return unchecked((int)code);
        }
    }

    /// <summary>
    /// Starts <paramref name="fileName"/> with <paramref name="arguments"/> under a job with resource caps.
    /// Children created by the process inherit the job limits when they do not break away.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ThrottledProcess Start(string fileName, string? arguments, ThrottleOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        options = (options ?? new ThrottleOptions()).Validate();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Job Object resource throttling requires Windows 8+ / Windows 11.");
        }

        var fullPath = ResolveExecutable(fileName);
        var commandLine = BuildCommandLine(fullPath, arguments);
        MinervaLog.Info($"Launch attempt path='{fullPath}' args='{arguments ?? ""}' | {MinervaLog.FormatOptions(options)}");

        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            var ex = new Win32Exception(err, "CreateJobObject failed.");
            MinervaLog.Error("Launch failed: CreateJobObject", ex);
            throw ex;
        }

        try
        {
            ConfigureJob(job, options);
            MinervaLog.Info("Job created; CPU/affinity/I/O/net limits applied (KillOnJobClose=on).");

            var startup = new NativeMethods.STARTUPINFO { cb = Marshal.SizeOf<NativeMethods.STARTUPINFO>() };
            var creationFlags = NativeMethods.CREATE_SUSPENDED | NativeMethods.CREATE_UNICODE_ENVIRONMENT;

            if (!NativeMethods.CreateProcessW(
                    fullPath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    bInheritHandles: false,
                    creationFlags,
                    IntPtr.Zero,
                    options.WorkingDirectory,
                    ref startup,
                    out var pi))
            {
                var ex = new Win32Exception(Marshal.GetLastWin32Error(), $"CreateProcess failed for '{fullPath}'.");
                MinervaLog.Error("Launch failed: CreateProcess", ex);
                throw ex;
            }

            try
            {
                if (!NativeMethods.AssignProcessToJobObject(job, pi.hProcess))
                {
                    var ex = new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed.");
                    MinervaLog.Error($"Launch failed: AssignProcessToJobObject (pid={pi.dwProcessId})", ex);
                    throw ex;
                }

                ApplyProcessPolicies(pi.hProcess, options);

                if (NativeMethods.ResumeThread(pi.hThread) == unchecked((uint)-1))
                {
                    var ex = new Win32Exception(Marshal.GetLastWin32Error(), "ResumeThread failed.");
                    MinervaLog.Error($"Launch failed: ResumeThread (pid={pi.dwProcessId})", ex);
                    throw ex;
                }

                var throttled = new ThrottledProcess(job, pi.hProcess, pi.hThread, unchecked((int)pi.dwProcessId), options);
                MinervaLog.Info($"Launch success pid={throttled.ProcessId} | applied: {MinervaLog.FormatOptions(options)}");
                // Ownership transferred.
                job = IntPtr.Zero;
                pi.hProcess = IntPtr.Zero;
                pi.hThread = IntPtr.Zero;
                return throttled;
            }
            finally
            {
                if (pi.hThread != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(pi.hThread);
                }

                if (pi.hProcess != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(pi.hProcess);
                }
            }
        }
        catch (Exception ex) when (ex is not Win32Exception and not PlatformNotSupportedException and not ArgumentException)
        {
            MinervaLog.Error("Launch failed", ex);
            throw;
        }
        finally
        {
            if (job != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(job);
            }
        }
    }

    /// <summary>
    /// Attempts to place an already-running process into a new throttled job.
    /// Fails if the process is already in a job that does not allow nesting/assignment.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static ThrottledProcess Attach(int processId, ThrottleOptions? options = null)
    {
        options = (options ?? new ThrottleOptions()).Validate();

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Job Object resource throttling requires Windows 8+ / Windows 11.");
        }

        MinervaLog.Info($"Attach attempt pid={processId} | {MinervaLog.FormatOptions(options)}");

        var access = NativeMethods.PROCESS_SET_INFORMATION
                     | NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION
                     | NativeMethods.PROCESS_SET_QUOTA
                     | NativeMethods.PROCESS_TERMINATE
                     | NativeMethods.PROCESS_SUSPEND_RESUME;

        var process = NativeMethods.OpenProcess(access, false, unchecked((uint)processId));
        if (process == IntPtr.Zero)
        {
            var ex = new Win32Exception(Marshal.GetLastWin32Error(), $"OpenProcess failed for PID {processId}.");
            MinervaLog.Error($"Attach failed: OpenProcess (pid={processId})", ex);
            throw ex;
        }

        var job = NativeMethods.CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            NativeMethods.CloseHandle(process);
            var ex = new Win32Exception(err, "CreateJobObject failed.");
            MinervaLog.Error($"Attach failed: CreateJobObject (pid={processId})", ex);
            throw ex;
        }

        try
        {
            ConfigureJob(job, options);
            MinervaLog.Info($"Job created for attach pid={processId}; limits applied (KillOnJobClose=on).");

            if (!NativeMethods.AssignProcessToJobObject(job, process))
            {
                var ex = new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "AssignProcessToJobObject failed. The process may already belong to another job.");
                MinervaLog.Error($"Attach failed: AssignProcessToJobObject (pid={processId})", ex);
                throw ex;
            }

            ApplyProcessPolicies(process, options);

            var throttled = new ThrottledProcess(job, process, IntPtr.Zero, processId, options);
            MinervaLog.Info($"Attach success pid={processId} | applied: {MinervaLog.FormatOptions(options)}");
            job = IntPtr.Zero;
            process = IntPtr.Zero;
            return throttled;
        }
        catch (Exception ex) when (ex is not Win32Exception and not PlatformNotSupportedException and not ArgumentException)
        {
            MinervaLog.Error($"Attach failed (pid={processId})", ex);
            throw;
        }
        finally
        {
            if (job != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(job);
            }

            if (process != IntPtr.Zero)
            {
                NativeMethods.CloseHandle(process);
            }
        }
    }

    public bool WaitForExit(int milliseconds = -1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var timeout = milliseconds < 0 ? NativeMethods.INFINITE : unchecked((uint)milliseconds);
        var result = NativeMethods.WaitForSingleObject(_processHandle, timeout);
        return result == 0;
    }

    public void Terminate(int exitCode = 1)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        MinervaLog.Info($"Terminate job requested for pid={ProcessId} exitCode={exitCode}");
        if (!NativeMethods.TerminateJobObject(_jobHandle, unchecked((uint)exitCode)))
        {
            var ex = new Win32Exception(Marshal.GetLastWin32Error(), "TerminateJobObject failed.");
            MinervaLog.Error($"TerminateJobObject failed (pid={ProcessId})", ex);
            throw ex;
        }

        MinervaLog.Info($"TerminateJobObject succeeded (pid={ProcessId})");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        MinervaLog.Info(
            $"Dispose job handles for pid={ProcessId} (KillOnJobClose=on — closing job may kill remaining members)");

        if (_threadHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_threadHandle);
        }

        if (_processHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_processHandle);
        }

        if (_jobHandle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_jobHandle);
        }
    }

    private bool GetExitCode(out uint code)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return NativeMethods.GetExitCodeProcess(_processHandle, out code);
    }

    private static void ConfigureJob(IntPtr job, ThrottleOptions options)
    {
        var cpu = new NativeMethods.JOBOBJECT_CPU_RATE_CONTROL_INFORMATION
        {
            ControlFlags = NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_ENABLE
                           | NativeMethods.JOB_OBJECT_CPU_RATE_CONTROL_HARD_CAP,
            CpuRate = options.ToJobCpuRate(),
        };

        SetJobInfo(job, NativeMethods.JobObjectCpuRateControlInformation, cpu);

        var extended = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        extended.BasicLimitInformation.LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

        if (options.AffinityCoreCount is int cores)
        {
            extended.BasicLimitInformation.LimitFlags |= NativeMethods.JOB_OBJECT_LIMIT_AFFINITY;
            extended.BasicLimitInformation.Affinity = AffinityMask.ForCoreCount(cores, Environment.ProcessorCount);
        }

        SetJobInfo(job, NativeMethods.JobObjectExtendedLimitInformation, extended);

        if (options.EffectiveDiskBandwidthBytesPerSecond is long diskBandwidth)
        {
            // Windows Job Objects expose one MaxBandwidth for all I/O (reads + writes share the pool).
            var io = new NativeMethods.JOBOBJECT_IO_RATE_CONTROL_INFORMATION
            {
                MaxIops = 0,
                MaxBandwidth = diskBandwidth,
                ReservationIops = 0,
                VolumeName = IntPtr.Zero,
                BaseIoSize = 0,
                ControlFlags = NativeMethods.JOB_OBJECT_IO_RATE_CONTROL_ENABLE,
            };

            try
            {
                SetJobInfo(job, NativeMethods.JobObjectIoRateControlInformation, io);
            }
            catch (Win32Exception ex)
            {
                var wrapped = new Win32Exception(
                    ex.NativeErrorCode,
                    "Job Object I/O rate control failed. Disk bandwidth limits require Windows 10+ " +
                    $"and may be unavailable in this session. Underlying error: {ex.Message}");
                MinervaLog.Error("SetInformationJobObject(IoRateControl) failed", wrapped);
                throw wrapped;
            }
        }

        if (options.NetworkTxBytesPerSecond is long netTx)
        {
            // Job Object net rate control caps outbound (Tx) bandwidth for members of the job.
            var net = new NativeMethods.JOBOBJECT_NET_RATE_CONTROL_INFORMATION
            {
                MaxBandwidth = unchecked((ulong)netTx),
                ControlFlags = NativeMethods.JOB_OBJECT_NET_RATE_CONTROL_ENABLE
                               | NativeMethods.JOB_OBJECT_NET_RATE_CONTROL_MAX_BANDWIDTH,
                DscpTag = 0,
            };

            try
            {
                SetJobInfo(job, NativeMethods.JobObjectNetRateControlInformation, net);
            }
            catch (Win32Exception ex)
            {
                var wrapped = new Win32Exception(
                    ex.NativeErrorCode,
                    "Job Object network rate control failed. Network Tx limits require Windows 10+ " +
                    $"(and typically work for TCP/UDP sockets owned by the job). Underlying error: {ex.Message}");
                MinervaLog.Error("SetInformationJobObject(NetRateControl) failed", wrapped);
                throw wrapped;
            }
        }
    }

    private static void ApplyProcessPolicies(IntPtr process, ThrottleOptions options)
    {
        var priority = options.Priority switch
        {
            ThrottlePriority.Idle => NativeMethods.IDLE_PRIORITY_CLASS,
            ThrottlePriority.BelowNormal => NativeMethods.BELOW_NORMAL_PRIORITY_CLASS,
            _ => NativeMethods.NORMAL_PRIORITY_CLASS,
        };

        if (!NativeMethods.SetPriorityClass(process, priority))
        {
            // Non-fatal: job hard cap still applies.
            var err = Marshal.GetLastWin32Error();
            MinervaLog.Warn($"SetPriorityClass({options.Priority}) failed (Win32 {err}); CPU hard cap still applies.");
            Debug.WriteLine($"SetPriorityClass failed: {err}");
        }

        if (options.EfficiencyMode)
        {
            TryEnableEfficiencyMode(process);
        }

        if (options.GpuThrottle != GpuThrottleMode.Off)
        {
            TrySetGpuPriority(process, options.GpuThrottle);
        }
    }

    /// <summary>
    /// Soft WDDM GPU scheduling hint. Not a hard GPU-% throttle — no public API provides one for arbitrary apps.
    /// </summary>
    private static void TrySetGpuPriority(IntPtr process, GpuThrottleMode mode)
    {
        var priorityClass = mode switch
        {
            GpuThrottleMode.Idle => NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_IDLE,
            GpuThrottleMode.BelowNormal => NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_BELOW_NORMAL,
            GpuThrottleMode.Normal => NativeMethods.D3DKMT_SCHEDULINGPRIORITYCLASS_NORMAL,
            _ => (int?)null,
        };

        if (priorityClass is not int clazz)
        {
            return;
        }

        try
        {
            var status = NativeMethods.D3DKMTSetProcessSchedulingPriorityClass(process, clazz);
            if (status != 0)
            {
                MinervaLog.Warn($"D3DKMTSetProcessSchedulingPriorityClass({mode}) returned NTSTATUS 0x{status:X8}");
                Debug.WriteLine($"D3DKMTSetProcessSchedulingPriorityClass({mode}) returned NTSTATUS 0x{status:X8}");
            }
            else
            {
                MinervaLog.Info($"GPU scheduling hint applied: {mode}");
            }
        }
        catch (DllNotFoundException ex)
        {
            MinervaLog.Warn($"GPU priority hint unavailable: {ex.Message}");
            Debug.WriteLine($"GPU priority hint unavailable: {ex.Message}");
        }
        catch (EntryPointNotFoundException ex)
        {
            MinervaLog.Warn($"GPU priority hint unavailable: {ex.Message}");
            Debug.WriteLine($"GPU priority hint unavailable: {ex.Message}");
        }
    }

    private static void TryEnableEfficiencyMode(IntPtr process)
    {
        var state = new NativeMethods.PROCESS_POWER_THROTTLING_STATE
        {
            Version = NativeMethods.PROCESS_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask = NativeMethods.PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
        };

        var size = Marshal.SizeOf(state);
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(state, ptr, false);
            if (!NativeMethods.SetProcessInformation(
                    process,
                    NativeMethods.ProcessPowerThrottling,
                    ptr,
                    (uint)size))
            {
                var err = Marshal.GetLastWin32Error();
                MinervaLog.Warn($"SetProcessInformation(EcoQoS) failed (Win32 {err}); CPU hard cap still applies.");
                Debug.WriteLine($"SetProcessInformation(EcoQoS) failed: {err}");
            }
            else
            {
                MinervaLog.Info("Efficiency Mode (EcoQoS) enabled.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static void SetJobInfo<T>(IntPtr job, int infoClass, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(value, ptr, false);
            if (!NativeMethods.SetInformationJobObject(job, infoClass, ptr, (uint)size))
            {
                var ex = new Win32Exception(Marshal.GetLastWin32Error(), $"SetInformationJobObject({infoClass}) failed.");
                MinervaLog.Error($"SetInformationJobObject({infoClass}) failed", ex);
                throw ex;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static string ResolveExecutable(string fileName)
    {
        if (Path.IsPathRooted(fileName) && File.Exists(fileName))
        {
            return Path.GetFullPath(fileName);
        }

        if (File.Exists(fileName))
        {
            return Path.GetFullPath(fileName);
        }

        // Allow launching via PATH on Windows (CreateProcess also searches PATH when lpApplicationName is null,
        // but we prefer a resolved path for clearer errors).
        var fromPath = FindOnPath(fileName);
        if (fromPath is not null)
        {
            return fromPath;
        }

        return Path.GetFullPath(fileName);
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';')
            : new[] { string.Empty };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in extensions)
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (!fileName.Contains('.', StringComparison.Ordinal) && !string.IsNullOrEmpty(ext))
                {
                    candidate += ext;
                }

                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static string BuildCommandLine(string executable, string? arguments)
    {
        var sb = new StringBuilder();
        QuoteArgument(sb, executable);
        if (!string.IsNullOrEmpty(arguments))
        {
            sb.Append(' ');
            sb.Append(arguments);
        }

        return sb.ToString();
    }

    private static void QuoteArgument(StringBuilder sb, string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            sb.Append(arg);
            return;
        }

        sb.Append('"');
        for (var i = 0; i < arg.Length; i++)
        {
            var c = arg[i];
            if (c == '"')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        sb.Append('"');
    }
}
