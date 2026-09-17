using CpuThrottle;

namespace CpuThrottle.Tray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        MinervaLog.EnsureStarted("CpuThrottle.Tray");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
