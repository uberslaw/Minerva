using System.Runtime.Versioning;

namespace CpuThrottle.Tray;

/// <summary>
/// Sortable running-process browser for attaching the current throttle options to an existing PID.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ProcessPickerForm : Form
{
    private readonly ListView _list = new();
    private readonly TextBox _filterBox = new();
    private readonly Button _refreshButton = new();
    private readonly Button _throttleButton = new();
    private readonly Button _cancelButton = new();
    private readonly Label _statusLabel = new();
    private readonly System.Windows.Forms.Timer _refreshTimer = new();
    private readonly ProcessListSampler _sampler = new();

    private IReadOnlyList<RunningProcessEntry> _entries = Array.Empty<RunningProcessEntry>();
    private ProcessSortColumn _sortColumn = ProcessSortColumn.CpuPercent;
    private bool _sortAscending;
    private bool _refreshInFlight;
    private int? _selectedPid;

    public ProcessPickerForm()
    {
        Text = "Running processes";
        Width = 820;
        Height = 520;
        MinimumSize = new Size(640, 400);
        StartPosition = FormStartPosition.CenterParent;
        Font = new Font("Segoe UI", 9F);
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = true;

        var filterLabel = new Label { Text = "Filter", AutoSize = true, Left = 12, Top = 16 };
        _filterBox.SetBounds(60, 12, 520, 27);
        _filterBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _filterBox.PlaceholderText = "Name, path, or PID…";
        _filterBox.TextChanged += (_, _) => RebuildList();

        _refreshButton.Text = "Refresh";
        _refreshButton.SetBounds(592, 10, 90, 30);
        _refreshButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refreshButton.Click += (_, _) => BeginRefresh();

        _list.SetBounds(12, 48, 780, 380);
        _list.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.MultiSelect = false;
        _list.GridLines = true;
        _list.Columns.Add("Name", 140);
        _list.Columns.Add("PID", 70, HorizontalAlignment.Right);
        _list.Columns.Add("CPU %", 70, HorizontalAlignment.Right);
        _list.Columns.Add("RAM", 90, HorizontalAlignment.Right);
        _list.Columns.Add("Path", 380);
        _list.ColumnClick += OnColumnClick;
        _list.SelectedIndexChanged += (_, _) => UpdateThrottleEnabled();
        _list.DoubleClick += (_, _) => AcceptSelection();

        _statusLabel.AutoSize = false;
        _statusLabel.SetBounds(12, 438, 480, 28);
        _statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        _statusLabel.Text = "Sampling… first refresh establishes CPU baselines.";
        _statusLabel.ForeColor = SystemColors.GrayText;

        _throttleButton.Text = "Throttle selected";
        _throttleButton.SetBounds(520, 434, 140, 32);
        _throttleButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _throttleButton.Enabled = false;
        _throttleButton.Click += (_, _) => AcceptSelection();

        _cancelButton.Text = "Cancel";
        _cancelButton.SetBounds(670, 434, 100, 32);
        _cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancelButton.Click += (_, _) =>
        {
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange(new Control[]
        {
            filterLabel, _filterBox, _refreshButton,
            _list, _statusLabel, _throttleButton, _cancelButton,
        });

        AcceptButton = _throttleButton;
        CancelButton = _cancelButton;

        _refreshTimer.Interval = 1500;
        _refreshTimer.Tick += (_, _) => BeginRefresh();

        Shown += (_, _) =>
        {
            BeginRefresh();
            _refreshTimer.Start();
        };
        FormClosed += (_, _) =>
        {
            _refreshTimer.Stop();
            _refreshTimer.Dispose();
        };
    }

    public int? SelectedProcessId => _selectedPid;

    private void OnColumnClick(object? sender, ColumnClickEventArgs e)
    {
        var column = e.Column switch
        {
            1 => ProcessSortColumn.ProcessId,
            2 => ProcessSortColumn.CpuPercent,
            3 => ProcessSortColumn.WorkingSet,
            4 => ProcessSortColumn.Path,
            _ => ProcessSortColumn.Name,
        };

        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            // CPU / RAM default to high-first; names/paths ascending.
            _sortAscending = column is ProcessSortColumn.Name or ProcessSortColumn.Path or ProcessSortColumn.ProcessId;
        }

        RebuildList();
    }

    private void BeginRefresh()
    {
        if (_refreshInFlight || IsDisposed)
        {
            return;
        }

        _refreshInFlight = true;
        _refreshButton.Enabled = false;
        _statusLabel.Text = "Refreshing…";

        _ = Task.Run(() =>
        {
            IReadOnlyList<RunningProcessEntry> sample;
            Exception? error = null;
            try
            {
                sample = _sampler.Sample();
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

                _refreshInFlight = false;
                _refreshButton.Enabled = true;
                if (error is not null)
                {
                    _statusLabel.Text = $"Refresh failed: {error.Message}";
                    return;
                }

                _entries = sample;
                RebuildList();
                _statusLabel.Text = $"{_list.Items.Count} processes — click a column header to sort.";
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

    private void RebuildList()
    {
        var filter = _filterBox.Text.Trim();
        IEnumerable<RunningProcessEntry> filtered = _entries;
        if (!string.IsNullOrEmpty(filter))
        {
            filtered = _entries.Where(e =>
                e.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || e.ProcessId.ToString().Contains(filter, StringComparison.Ordinal));
        }

        var sorted = ProcessListSorter.Sort(filtered, _sortColumn, _sortAscending);
        var previousPid = _list.SelectedItems.Count > 0
            && int.TryParse(_list.SelectedItems[0].SubItems[1].Text, out var pid)
                ? pid
                : (int?)null;

        _list.BeginUpdate();
        try
        {
            _list.Items.Clear();
            foreach (var entry in sorted)
            {
                var item = new ListViewItem(entry.Name);
                item.SubItems.Add(entry.ProcessId.ToString());
                item.SubItems.Add(ProcessListFormatter.FormatCpuPercent(entry.CpuPercent));
                item.SubItems.Add(ProcessListFormatter.FormatWorkingSet(entry.WorkingSetBytes));
                item.SubItems.Add(string.IsNullOrEmpty(entry.Path) ? "(unavailable)" : entry.Path);
                item.Tag = entry.ProcessId;
                _list.Items.Add(item);
                if (previousPid == entry.ProcessId)
                {
                    item.Selected = true;
                    item.EnsureVisible();
                }
            }
        }
        finally
        {
            _list.EndUpdate();
        }

        UpdateThrottleEnabled();
    }

    private void UpdateThrottleEnabled()
    {
        _throttleButton.Enabled = _list.SelectedItems.Count > 0;
    }

    private void AcceptSelection()
    {
        if (_list.SelectedItems.Count == 0)
        {
            return;
        }

        if (_list.SelectedItems[0].Tag is int pid)
        {
            _selectedPid = pid;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Avoid designer warnings when Host is non-Windows CI; form is Windows-only.
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Process picker requires Windows.");
        }
    }
}
