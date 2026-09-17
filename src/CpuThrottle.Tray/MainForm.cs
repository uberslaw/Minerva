using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using CpuThrottle;

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
    private readonly TrackBar _gpuSlider = new();
    private readonly Label _gpuValueLabel = new();
    private readonly Button _browseButton = new();
    private readonly Button _launchButton = new();
    private readonly Button _pickProcessButton = new();
    private readonly Button _throttleSelectedButton = new();
    private readonly Button _refreshProcessesButton = new();
    private readonly Button _stopButton = new();
    private readonly Label _statusLabel = new();
    private readonly TextBox _processFilterBox = new();
    private readonly ListView _processList = new();
    private readonly Label _processStatusLabel = new();
    private readonly NotifyIcon _trayIcon;
    private readonly Icon _appIcon;
    private readonly System.Windows.Forms.Timer _monitorTimer = new();
    private readonly System.Windows.Forms.Timer _processRefreshTimer = new();
    private readonly ProcessListSampler _processSampler = new();

    private ThrottledProcess? _running;
    private ProcessCpuMonitor? _monitor;
    private string? _lastExe;
    private IReadOnlyList<RunningProcessEntry> _processEntries = Array.Empty<RunningProcessEntry>();
    private ProcessSortColumn _processSortColumn = ProcessSortColumn.CpuPercent;
    private bool _processSortAscending;
    private bool _processRefreshInFlight;
    private int _cpuSampleTick;

    public MainForm()
    {
        _appIcon = LoadAppIcon();
        Text = "CpuThrottle";
        Icon = _appIcon;
        Width = 720;
        Height = 780;
        MinimumSize = new Size(640, 700);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;
        Font = new Font("Segoe UI", 9F);

        var exeLabel = new Label { Text = "Executable", AutoSize = true, Left = 16, Top = 18 };
        _exeBox.SetBounds(16, 40, 560, 27);
        _exeBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _browseButton.Text = "Browse…";
        _browseButton.SetBounds(588, 38, 100, 30);
        _browseButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _browseButton.Click += (_, _) => BrowseForExe();

        var argsLabel = new Label { Text = "Arguments", AutoSize = true, Left = 16, Top = 78 };
        _argsBox.SetBounds(16, 100, 672, 27);
        _argsBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

        var cpuLabel = new Label { Text = "CPU hard cap", AutoSize = true, Left = 16, Top = 140 };
        _cpuSlider.SetBounds(16, 162, 580, 45);
        _cpuSlider.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _cpuSlider.Minimum = 10;
        _cpuSlider.Maximum = 90;
        _cpuSlider.TickFrequency = 10;
        _cpuSlider.Value = 50;
        _cpuSlider.ValueChanged += (_, _) => UpdateCpuLabel();
        _cpuValueLabel.AutoSize = true;
        _cpuValueLabel.Left = 610;
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

        var gpuLabel = new Label { Text = "GPU priority (best-effort)", AutoSize = true, Left = 16, Top = 282 };
        _gpuSlider.SetBounds(16, 304, 520, 45);
        _gpuSlider.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _gpuSlider.Minimum = 0;
        _gpuSlider.Maximum = 3;
        _gpuSlider.TickFrequency = 1;
        _gpuSlider.SmallChange = 1;
        _gpuSlider.LargeChange = 1;
        _gpuSlider.Value = 0;
        _gpuSlider.ValueChanged += (_, _) => UpdateGpuLabel();
        _gpuValueLabel.AutoSize = true;
        _gpuValueLabel.Left = 550;
        _gpuValueLabel.Top = 312;
        _gpuValueLabel.Anchor = AnchorStyles.Top | AnchorStyles.Right;

        var diskReadLabel = new Label { Text = "Disk read KB/s (0 = off)", AutoSize = true, Left = 16, Top = 352 };
        _diskReadBox.SetBounds(170, 348, 90, 27);
        _diskReadBox.Minimum = 0;
        _diskReadBox.Maximum = 100_000_000;
        _diskReadBox.Increment = 1024;
        _diskReadBox.Value = 0;

        var diskWriteLabel = new Label { Text = "Disk write KB/s (0 = off)", AutoSize = true, Left = 280, Top = 352 };
        _diskWriteBox.SetBounds(440, 348, 90, 27);
        _diskWriteBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _diskWriteBox.Minimum = 0;
        _diskWriteBox.Maximum = 100_000_000;
        _diskWriteBox.Increment = 1024;
        _diskWriteBox.Value = 0;

        var networkLabel = new Label { Text = "Network Tx KB/s (0 = off)", AutoSize = true, Left = 16, Top = 388 };
        _networkTxBox.SetBounds(180, 384, 90, 27);
        _networkTxBox.Minimum = 0;
        _networkTxBox.Maximum = 100_000_000;
        _networkTxBox.Increment = 1024;
        _networkTxBox.Value = 0;

        var noteLabel = new Label
        {
            Text = "Disk read/write share one Job Object MaxBandwidth; network Tx is outbound-only; GPU slider is a soft WDDM priority hint (not a hard GPU %).",
            AutoSize = false,
            Left = 16,
            Top = 418,
            Width = 672,
            Height = 32,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            ForeColor = SystemColors.GrayText,
        };

        var processSectionLabel = new Label
        {
            Text = "Running processes — select one and click Throttle selected, or open the full picker.",
            AutoSize = true,
            Left = 16,
            Top = 456,
        };

        var filterLabel = new Label { Text = "Filter", AutoSize = true, Left = 16, Top = 486 };
        _processFilterBox.SetBounds(60, 482, 500, 27);
        _processFilterBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _processFilterBox.PlaceholderText = "Name, path, or PID…";
        _processFilterBox.TextChanged += (_, _) => RebuildProcessList();

        _refreshProcessesButton.Text = "Refresh";
        _refreshProcessesButton.SetBounds(572, 480, 116, 30);
        _refreshProcessesButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refreshProcessesButton.Click += (_, _) => BeginProcessRefresh();

        _processList.SetBounds(16, 518, 672, 140);
        _processList.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _processList.View = View.Details;
        _processList.FullRowSelect = true;
        _processList.HideSelection = false;
        _processList.MultiSelect = false;
        _processList.GridLines = true;
        _processList.Columns.Add("Name", 160);
        _processList.Columns.Add("PID", 70, HorizontalAlignment.Right);
        _processList.Columns.Add("CPU %", 70, HorizontalAlignment.Right);
        _processList.Columns.Add("RAM", 90, HorizontalAlignment.Right);
        _processList.Columns.Add("Path", 250);
        _processList.ColumnClick += OnProcessColumnClick;
        _processList.SelectedIndexChanged += (_, _) => UpdateThrottleSelectedEnabled();
        _processList.DoubleClick += (_, _) => ThrottleSelectedFromList();

        _processStatusLabel.AutoSize = false;
        _processStatusLabel.SetBounds(16, 664, 672, 20);
        _processStatusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _processStatusLabel.ForeColor = SystemColors.GrayText;
        _processStatusLabel.Text = "Sampling processes…";

        _launchButton.Text = "Launch throttled";
        _launchButton.SetBounds(16, 692, 130, 32);
        _launchButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _launchButton.Click += (_, _) => Launch();

        _pickProcessButton.Text = "Pick process…";
        _pickProcessButton.SetBounds(154, 692, 120, 32);
        _pickProcessButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _pickProcessButton.Click += (_, _) => AttachFromPickerDialog();

        _throttleSelectedButton.Text = "Throttle selected";
        _throttleSelectedButton.SetBounds(282, 692, 130, 32);
        _throttleSelectedButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _throttleSelectedButton.Enabled = false;
        _throttleSelectedButton.Click += (_, _) => ThrottleSelectedFromList();

        _stopButton.Text = "Stop job";
        _stopButton.SetBounds(420, 692, 100, 32);
        _stopButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        _stopButton.Enabled = false;
        _stopButton.Click += (_, _) => StopJob();

        _statusLabel.AutoSize = false;
        _statusLabel.SetBounds(16, 732, 672, 28);
        _statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _statusLabel.Text = "Ready — launch an .exe, or pick a running process from the list below.";

        Controls.AddRange(new Control[]
        {
            exeLabel, _exeBox, _browseButton,
            argsLabel, _argsBox,
            cpuLabel, _cpuSlider, _cpuValueLabel,
            coresLabel, _coresBox, _efficiencyBox,
            priorityLabel, _priorityBox,
            gpuLabel, _gpuSlider, _gpuValueLabel,
            diskReadLabel, _diskReadBox, diskWriteLabel, _diskWriteBox,
            networkLabel, _networkTxBox, noteLabel,
            processSectionLabel, filterLabel, _processFilterBox, _refreshProcessesButton,
            _processList, _processStatusLabel,
            _launchButton, _pickProcessButton, _throttleSelectedButton, _stopButton, _statusLabel,
        });

        _trayIcon = new NotifyIcon
        {
            Text = "CpuThrottle",
            Visible = true,
            Icon = _appIcon,
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

        _processRefreshTimer.Interval = 2000;
        _processRefreshTimer.Tick += (_, _) => BeginProcessRefresh();

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
        Shown += (_, _) =>
        {
            BeginProcessRefresh();
            _processRefreshTimer.Start();
        };

        UpdateCpuLabel();
        UpdateGpuLabel();
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
        menu.Items.Add("Pick process…", null, (_, _) =>
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            AttachFromPickerDialog();
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

    private void UpdateGpuLabel() => _gpuValueLabel.Text = DescribeGpuSlider(_gpuSlider.Value);

    private static string DescribeGpuSlider(int value) => value switch
    {
        1 => "Idle",
        2 => "Below normal",
        3 => "Normal",
        _ => "Off",
    };

    private GpuThrottleMode SelectedGpuMode() => _gpuSlider.Value switch
    {
        1 => GpuThrottleMode.Idle,
        2 => GpuThrottleMode.BelowNormal,
        3 => GpuThrottleMode.Normal,
        _ => GpuThrottleMode.Off,
    };

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
            var options = BuildOptions();
            MinervaLog.Info($"UI Launch clicked path='{exe}' args='{args ?? ""}' | {MinervaLog.FormatOptions(options)}");
            BeginJob(ThrottledProcess.Start(exe, args, options), exe, attached: false);
        }
        catch (Exception ex)
        {
            MinervaLog.Error("UI Launch failed", ex);
            MessageBox.Show(this, ex.Message, "Launch failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            CleanupRunning();
        }
    }

    private void AttachFromPickerDialog()
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

        AttachPid(pid);
    }

    private void ThrottleSelectedFromList()
    {
        if (!EnsureCanStartJob())
        {
            return;
        }

        if (_processList.SelectedItems.Count == 0 || _processList.SelectedItems[0].Tag is not int pid)
        {
            MessageBox.Show(
                this,
                "Select a process in the list, or click Pick process… for the full picker.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        AttachPid(pid);
    }

    private void AttachPid(int pid)
    {
        try
        {
            var options = BuildOptions();
            MinervaLog.Info($"UI Attach clicked pid={pid} | {MinervaLog.FormatOptions(options)}");
            BeginJob(ThrottledProcess.Attach(pid, options), exePath: null, attached: true);
        }
        catch (Exception ex)
        {
            MinervaLog.Error($"UI Attach failed (pid={pid})", ex);
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
        GpuThrottle = SelectedGpuMode(),
    };

    private void BeginJob(ThrottledProcess process, string? exePath, bool attached)
    {
        _running = process;
        _cpuSampleTick = 0;
        if (!string.IsNullOrWhiteSpace(exePath))
        {
            _lastExe = exePath;
        }

        _monitor = new ProcessCpuMonitor(_running.ProcessId);
        _ = _monitor.SampleCpuPercent();
        _monitorTimer.Start();
        SetJobControlsEnabled(jobRunning: true);
        var verb = attached ? "Attached" : "Running";
        var gpu = DescribeGpuSlider(_gpuSlider.Value);
        _statusLabel.Text = $"{verb} PID {_running.ProcessId} @ CPU {_cpuSlider.Value}%, GPU {gpu}";
        _trayIcon.Text = $"CpuThrottle — PID {_running.ProcessId}";
        _trayIcon.ShowBalloonTip(
            2000,
            "CpuThrottle",
            $"{verb} under {_cpuSlider.Value}% CPU cap (GPU {gpu}).",
            ToolTipIcon.Info);
        MinervaLog.Info($"UI {verb} job pid={_running.ProcessId} | {MinervaLog.FormatOptions(_running.Options)} | log={MinervaLog.LogFilePath}");
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

        var pid = _running.ProcessId;
        MinervaLog.Info($"UI Stop job clicked pid={pid}");
        try
        {
            _running.Terminate();
        }
        catch (Win32Exception ex)
        {
            MinervaLog.Error($"UI Stop TerminateJobObject failed (pid={pid})", ex);
            Debug.WriteLine(ex);
        }

        CleanupRunning();
        _statusLabel.Text = "Job stopped.";
        MinervaLog.Info($"UI job stopped pid={pid}");
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
                var pid = _running.ProcessId;
                CleanupRunning();
                _statusLabel.Text = $"Exited with code {code}.";
                MinervaLog.Info($"UI observed exit pid={pid} code={code}");
                return;
            }

            var cpu = _monitor.SampleCpuPercent();
            _statusLabel.Text = $"PID {_running.ProcessId} — ~{cpu:0.0}% CPU (cap {_running.Options.CpuPercent}%)";
            _trayIcon.Text = $"CpuThrottle — {cpu:0.0}% / {_running.Options.CpuPercent}%";
            _cpuSampleTick++;
            if (_cpuSampleTick % 10 == 0)
            {
                MinervaLog.Info($"CPU sample pid={_running.ProcessId} ~{cpu:0.0}% (cap {_running.Options.CpuPercent}%)");
            }
        }
        catch (Exception ex)
        {
            MinervaLog.Warn($"UI status refresh ended monitoring: {ex.Message}");
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
        SetJobControlsEnabled(jobRunning: false);
        _trayIcon.Text = "CpuThrottle";
    }

    private void SetJobControlsEnabled(bool jobRunning)
    {
        _launchButton.Enabled = !jobRunning;
        _pickProcessButton.Enabled = !jobRunning;
        _throttleSelectedButton.Enabled = !jobRunning && _processList.SelectedItems.Count > 0;
        _stopButton.Enabled = jobRunning;
    }

    private void UpdateThrottleSelectedEnabled()
    {
        _throttleSelectedButton.Enabled = _running is null && _processList.SelectedItems.Count > 0;
    }

    private void OnProcessColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var column = e.Column switch
        {
            1 => ProcessSortColumn.ProcessId,
            2 => ProcessSortColumn.CpuPercent,
            3 => ProcessSortColumn.WorkingSet,
            4 => ProcessSortColumn.Path,
            _ => ProcessSortColumn.Name,
        };

        if (_processSortColumn == column)
        {
            _processSortAscending = !_processSortAscending;
        }
        else
        {
            _processSortColumn = column;
            _processSortAscending = column is ProcessSortColumn.Name or ProcessSortColumn.Path or ProcessSortColumn.ProcessId;
        }

        RebuildProcessList();
    }

    private void BeginProcessRefresh()
    {
        if (_processRefreshInFlight || IsDisposed)
        {
            return;
        }

        _processRefreshInFlight = true;
        _refreshProcessesButton.Enabled = false;
        _processStatusLabel.Text = "Refreshing process list…";

        _ = Task.Run(() =>
        {
            IReadOnlyList<RunningProcessEntry> sample;
            Exception? error = null;
            try
            {
                sample = _processSampler.Sample();
            }
            catch (Exception ex)
            {
                sample = Array.Empty<RunningProcessEntry>();
                error = ex;
            }

            PostToUi(() =>
            {
                if (IsDisposed)
                {
                    return;
                }

                _processRefreshInFlight = false;
                _refreshProcessesButton.Enabled = true;
                if (error is not null)
                {
                    _processStatusLabel.Text = $"Refresh failed: {error.Message}";
                    return;
                }

                _processEntries = sample;
                RebuildProcessList();
                _processStatusLabel.Text = $"{_processList.Items.Count} processes — double-click or use Throttle selected.";
            });
        });
    }

    private void PostToUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
                // Form closed while sample was in flight.
            }

            return;
        }

        action();
    }

    private void RebuildProcessList()
    {
        var filter = _processFilterBox.Text.Trim();
        IEnumerable<RunningProcessEntry> filtered = _processEntries;
        if (!string.IsNullOrEmpty(filter))
        {
            filtered = _processEntries.Where(e =>
                e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.ProcessId.ToString().Contains(filter, StringComparison.Ordinal));
        }

        var sorted = ProcessListSorter.Sort(filtered, _processSortColumn, _processSortAscending);
        var previousPid = _processList.SelectedItems.Count > 0
            && int.TryParse(_processList.SelectedItems[0].SubItems[1].Text, out var pid)
                ? pid
                : (int?)null;

        _processList.BeginUpdate();
        try
        {
            _processList.Items.Clear();
            foreach (var entry in sorted)
            {
                var item = new ListViewItem(entry.Name);
                item.SubItems.Add(entry.ProcessId.ToString());
                item.SubItems.Add(ProcessListFormatter.FormatCpuPercent(entry.CpuPercent));
                item.SubItems.Add(ProcessListFormatter.FormatWorkingSet(entry.WorkingSetBytes));
                item.SubItems.Add(string.IsNullOrEmpty(entry.Path) ? "(unavailable)" : entry.Path);
                item.Tag = entry.ProcessId;
                _processList.Items.Add(item);
                if (previousPid == entry.ProcessId)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
            }
        }
        finally
        {
            _processList.EndUpdate();
        }

        UpdateThrottleSelectedEnabled();
    }

    private static Icon LoadAppIcon()
    {
        var assembly = typeof(MainForm).Assembly;
        using var stream = assembly.GetManifestResourceStream("CpuThrottle.Tray.Assets.minerva.ico");
        if (stream is null)
        {
            return SystemIcons.Application;
        }

        // Clone so the icon owns its data after the stream is disposed.
        using var loaded = new Icon(stream);
        return (Icon)loaded.Clone();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _processRefreshTimer.Stop();
        _processRefreshTimer.Dispose();
        CleanupRunning();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _monitorTimer.Dispose();
        if (!ReferenceEquals(_appIcon, SystemIcons.Application))
        {
            _appIcon.Dispose();
        }
    }
}
