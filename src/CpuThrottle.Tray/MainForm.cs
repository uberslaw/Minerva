using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;

namespace CpuThrottle.Tray;

[SupportedOSPlatform("windows")]
internal sealed class MainForm : Form
{
    private readonly TextBox _exeBox = new();
    private readonly TextBox _argsBox = new();
    private readonly TrackBar _cpuSlider = new();
    private readonly Label _cpuValueLabel = new();
    private readonly NumericUpDown _coresBox = new();
    private readonly CheckBox _efficiencyBox = new();
    private readonly ComboBox _priorityBox = new();
    private readonly NumericUpDown _diskReadBox = new();
    private readonly NumericUpDown _diskWriteBox = new();
    private readonly NumericUpDown _networkTxBox = new();
    private readonly CheckBox _gpuLowBox = new();
    private readonly Button _browseButton = new();
    private readonly Button _launchButton = new();
    private readonly Button _attachButton = new();
    private readonly Button _stopButton = new();
    private readonly Label _statusLabel = new();
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _monitorTimer = new();

    private ThrottledProcess? _running;
    private ProcessCpuMonitor? _monitor;
    private string? _lastExe;

    public MainForm()
    {
        Text = "CpuThrottle";
        Width = 560;
        Height = 520;
        MinimumSize = new Size(520, 480);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        Font = new Font("Segoe UI", 9F);

        var exeLabel = new Label { Text = "Executable", AutoSize = true, Left = 16, Top = 18 };
        _exeBox.SetBounds(16, 40, 400, 27);
        _exeBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _browseButton.Text = "Browse…";
        _browseButton.SetBounds(428, 38, 100, 30);
        _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _browseButton.Click += (_, _) => BrowseForExe();

        var argsLabel = new Label { Text = "Arguments", AutoSize = true, Left = 16, Top = 78 };
        _argsBox.SetBounds(16, 100, 512, 27);
        _argsBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var cpuLabel = new Label { Text = "CPU hard cap", AutoSize = true, Left = 16, Top = 140 };
        _cpuSlider.SetBounds(16, 162, 440, 45);
        _cpuSlider.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _cpuSlider.Minimum = 10;
        _cpuSlider.Maximum = 90;
        _cpuSlider.TickFrequency = 10;
        _cpuSlider.Value = 50;
        _cpuSlider.ValueChanged += (_, _) => UpdateCpuLabel();
        _cpuValueLabel.AutoSize = true;
        _cpuValueLabel.Left = 470;
        _cpuValueLabel.Top = 170;
        _cpuValueLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        var coresLabel = new Label { Text = "Affinity cores (0 = all)", AutoSize = true, Left = 16, Top = 210 };
        _coresBox.SetBounds(180, 206, 60, 27);
        _coresBox.Minimum = 0;
        _coresBox.Maximum = Environment.ProcessorCount;
        _coresBox.Value = 0;

        _efficiencyBox.Text = "Efficiency Mode (EcoQoS)";
        _efficiencyBox.AutoSize = true;
        _efficiencyBox.Checked = true;
        _efficiencyBox.Left = 260;
        _efficiencyBox.Top = 210;

        var priorityLabel = new Label { Text = "Priority", AutoSize = true, Left = 16, Top = 246 };
        _priorityBox.SetBounds(80, 242, 140, 27);
        _priorityBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _priorityBox.Items.AddRange(new object[] { "Idle", "Below normal", "Normal" });
        _priorityBox.SelectedIndex = 1;

        _gpuLowBox.Text = "GPU low priority (best-effort)";
        _gpuLowBox.AutoSize = true;
        _gpuLowBox.Left = 240;
        _gpuLowBox.Top = 246;

        var diskReadLabel = new Label { Text = "Disk read KB/s (0 = off)", AutoSize = true, Left = 16, Top = 286 };
        _diskReadBox.SetBounds(170, 282, 90, 27);
        _diskReadBox.Minimum = 0;
        _diskReadBox.Maximum = 100_000_000;
        _diskReadBox.Increment = 1024;
        _diskReadBox.Value = 0;

        var diskWriteLabel = new Label { Text = "Disk write KB/s (0 = off)", AutoSize = true, Left = 280, Top = 286 };
        _diskWriteBox.SetBounds(440, 282, 90, 27);
        _diskWriteBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _diskWriteBox.Minimum = 0;
        _diskWriteBox.Maximum = 100_000_000;
        _diskWriteBox.Increment = 1024;
        _diskWriteBox.Value = 0;

        var networkLabel = new Label { Text = "Network Tx KB/s (0 = off)", AutoSize = true, Left = 16, Top = 322 };
        _networkTxBox.SetBounds(180, 318, 90, 27);
        _networkTxBox.Minimum = 0;
        _networkTxBox.Maximum = 100_000_000;
        _networkTxBox.Increment = 1024;
        _networkTxBox.Value = 0;

        var noteLabel = new Label
        {
            Text = "Disk read/write share one Job Object MaxBandwidth; network Tx is outbound-only; GPU is a soft hint.",
            AutoSize = false,
            Left = 16,
            Top = 352,
            Width = 512,
            Height = 32,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
        };

        _launchButton.Text = "Launch throttled";
        _launchButton.SetBounds(16, 396, 130, 32);
        _launchButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _launchButton.Click += (_, _) => Launch();

        _attachButton.Text = "Attach…";
        _attachButton.SetBounds(154, 396, 100, 32);
        _attachButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _attachButton.Click += (_, _) => AttachFromList();

        _stopButton.Text = "Stop job";
        _stopButton.SetBounds(262, 396, 100, 32);
        _stopButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopJob();

        _statusLabel.AutoSize = false;
        _statusLabel.SetBounds(16, 440, 512, 28);
        _statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _statusLabel.Text = "Ready — drop an .exe, browse, or Attach to a running process.";

        Controls.AddRange(new Control[]
        {
            exeLabel, _exeBox, _browseButton,
            argsLabel, _argsBox,
            cpuLabel, _cpuSlider, _cpuValueLabel,
            coresLabel, _coresBox, _efficiencyBox,
            priorityLabel, _priorityBox, _gpuLowBox,
            diskReadLabel, _diskReadBox, diskWriteLabel, _diskWriteBox,
            networkLabel, _networkTxBox, noteLabel,
            _launchButton, _attachButton, _stopButton, _statusLabel,
        });

        _trayIcon = new NotifyIcon
        {
            Text = "CpuThrottle",
            Visible = true,
            Icon = SystemIcons.Application,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _trayIcon.DoubleClick += (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        };

        _monitorTimer.Interval = 1000;
        _monitorTimer.Tick += (_, _) => RefreshStatus();

        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized)
            {
                Hide();
            }
        };
        FormClosing += OnFormClosing;

        UpdateCpuLabel();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        });
        menu.Items.Add("Launch last", null, (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_lastExe))
            {
                _exeBox.Text = _lastExe;
                Launch();
            }
        });
        menu.Items.Add("Attach to process…", null, (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            AttachFromList();
        });
        menu.Items.Add("Stop job", null, (_, _) => StopJob());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _trayIcon.Visible = false;
            Application.Exit();
        });
        return menu;
    }

    private void UpdateCpuLabel() => _cpuValueLabel.Text = $"{_cpuSlider.Value}%";

    private void BrowseForExe()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "Executables (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "Select modelling application",
        };
        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _exeBox.Text = dialog.FileName;
        }
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            e.Effect = DragDropEffects.Copy;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            _exeBox.Text = files[0];
        }
    }

    private void Launch()
    {
        if (!EnsureCanStartJob())
        {
            return;
        }

        var exe = _exeBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(exe))
        {
            MessageBox.Show(this, "Choose an executable to launch.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var args = string.IsNullOrWhiteSpace(_argsBox.Text) ? null : _argsBox.Text.Trim();
            BeginJob(ThrottledProcess.Start(exe, args, BuildOptions()), exe, attached: false);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            CleanupRunning();
        }
    }

    private void AttachFromList()
    {
        if (!EnsureCanStartJob())
        {
            return;
        }

        using var picker = new ProcessPickerForm();
        if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedProcessId is not int pid)
        {
            return;
        }

        try
        {
            BeginJob(ThrottledProcess.Attach(pid, BuildOptions()), exePath: null, attached: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                ProcessListFormatter.DescribeAttachFailure(ex, pid),
                "Attach failed",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            CleanupRunning();
        }
    }

    private bool EnsureCanStartJob()
    {
        if (_running is not null)
        {
            MessageBox.Show(this, "A throttled job is already running. Stop it first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            MessageBox.Show(this, "CpuThrottle requires Windows.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }

        return true;
    }

    private ThrottleOptions BuildOptions() => new()
    {
        CpuPercent = _cpuSlider.Value,
        AffinityCoreCount = _coresBox.Value > 0 ? (int)_coresBox.Value : null,
        EfficiencyMode = _efficiencyBox.Checked,
        Priority = _priorityBox.SelectedIndex switch
        {
            0 => ThrottlePriority.Idle,
            2 => ThrottlePriority.Normal,
            _ => ThrottlePriority.BelowNormal,
        },
        DiskReadBytesPerSecond = KbPerSecToBytes(_diskReadBox.Value),
        DiskWriteBytesPerSecond = KbPerSecToBytes(_diskWriteBox.Value),
        NetworkTxBytesPerSecond = KbPerSecToBytes(_networkTxBox.Value),
        GpuThrottle = _gpuLowBox.Checked ? GpuThrottleMode.LowPriority : GpuThrottleMode.Off,
    };

    private void BeginJob(ThrottledProcess process, string? exePath, bool attached)
    {
        _running = process;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            _lastExe = exePath;
        }

        _monitor = new ProcessCpuMonitor(_running.ProcessId);
        _ = _monitor.SampleCpuPercent();
        _monitorTimer.Start();
        _launchButton.Enabled = false;
        _attachButton.Enabled = false;
        _stopButton.Enabled = true;
        var verb = attached ? "Attached" : "Running";
        _statusLabel.Text = $"{verb} PID {_running.ProcessId} @ cap {_cpuSlider.Value}%";
        _trayIcon.Text = $"CpuThrottle — PID {_running.ProcessId}";
        _trayIcon.ShowBalloonTip(
            2000,
            "CpuThrottle",
            $"{verb} under {_cpuSlider.Value}% CPU cap.",
            ToolTipIcon.Info);
    }

    private static long? KbPerSecToBytes(decimal kbPerSec)
    {
        if (kbPerSec <= 0)
        {
            return null;
        }

        return (long)kbPerSec * 1024L;
    }

    private void StopJob()
    {
        if (_running is null)
        {
            return;
        }

        try
        {
            _running.Terminate();
        }
        catch (Win32Exception ex)
        {
            Debug.WriteLine(ex);
        }

        CleanupRunning();
        _statusLabel.Text = "Job stopped.";
    }

    private void RefreshStatus()
    {
        if (_running is null || _monitor is null)
        {
            return;
        }

        try
        {
            if (_running.HasExited)
            {
                var code = _running.ExitCode;
                CleanupRunning();
                _statusLabel.Text = $"Exited with code {code}.";
                return;
            }

            var cpu = _monitor.SampleCpuPercent();
            _statusLabel.Text = $"PID {_running.ProcessId} — ~{cpu:0.0}% CPU (cap {_running.Options.CpuPercent}%)";
            _trayIcon.Text = $"CpuThrottle — {cpu:0.0}% / {_running.Options.CpuPercent}%";
        }
        catch
        {
            CleanupRunning();
            _statusLabel.Text = "Process ended.";
        }
    }

    private void CleanupRunning()
    {
        _monitorTimer.Stop();
        _monitor?.Dispose();
        _monitor = null;
        _running?.Dispose();
        _running = null;
        _launchButton.Enabled = true;
        _attachButton.Enabled = true;
        _stopButton.Enabled = false;
        _trayIcon.Text = "CpuThrottle";
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        CleanupRunning();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _monitorTimer.Dispose();
    }
}
