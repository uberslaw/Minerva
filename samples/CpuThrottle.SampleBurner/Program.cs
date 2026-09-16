using System.Diagnostics;

namespace CpuThrottle.SampleBurner;

/// <summary>
/// Multi-threaded CPU burner used to validate Job Object hard caps on Windows.
/// Spawns worker threads and optional child processes to mimic modelling process trees.
/// </summary>
internal static class Program
{
    private static long _ops;

    private static int Main(string[] args)
    {
        var seconds = GetInt(args, "--seconds", 30);
        var threads = GetInt(args, "--threads", Math.Max(1, Environment.ProcessorCount));
        var children = GetInt(args, "--children", 0);
        var childSeconds = GetInt(args, "--child-seconds", seconds);

        Console.WriteLine($"SampleBurner: threads={threads}, seconds={seconds}, children={children}, processors={Environment.ProcessorCount}");

        var childProcesses = new List<Process>();
        try
        {
            for (var i = 0; i < children; i++)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = Environment.ProcessPath ?? "CpuThrottle.SampleBurner",
                    ArgumentList = { "--seconds", childSeconds.ToString(), "--threads", Math.Max(1, threads / 2).ToString() },
                    UseShellExecute = false,
                };
                childProcesses.Add(Process.Start(psi)!);
                Console.WriteLine($"Spawned child PID {childProcesses[^1].Id}");
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            var workers = new Task[threads];
            for (var t = 0; t < threads; t++)
            {
                workers[t] = Task.Run(() => Burn(cts.Token), cts.Token);
            }

            var sw = Stopwatch.StartNew();
            while (!cts.IsCancellationRequested)
            {
                Thread.Sleep(1000);
                var ops = Interlocked.Read(ref _ops);
                Console.WriteLine($"t={sw.Elapsed.TotalSeconds:0.0}s ops={ops}");
            }

            try
            {
                Task.WaitAll(workers);
            }
            catch (AggregateException)
            {
                // Cancellation is expected.
            }

            foreach (var child in childProcesses)
            {
                if (!child.HasExited)
                {
                    child.WaitForExit(childSeconds * 1000 + 5000);
                }
            }

            Console.WriteLine($"Done. Total ops={Interlocked.Read(ref _ops)}");
            return 0;
        }
        finally
        {
            foreach (var child in childProcesses)
            {
                try
                {
                    if (!child.HasExited)
                    {
                        child.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore cleanup failures.
                }

                child.Dispose();
            }
        }
    }

    private static void Burn(CancellationToken token)
    {
        var x = 1.0000001;
        while (!token.IsCancellationRequested)
        {
            for (var i = 0; i < 10_000; i++)
            {
                x = Math.Sqrt(x) * 1.0000001 + 0.0000001;
            }

            Interlocked.Increment(ref _ops);
        }

        _ = x;
    }

    private static int GetInt(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var value)
                && value > 0)
            {
                return value;
            }
        }

        return fallback;
    }
}
