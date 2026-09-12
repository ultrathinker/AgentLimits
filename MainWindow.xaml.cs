using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using DrawIcon = System.Drawing.Icon;
using DrawColor = System.Drawing.Color;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using AgentLimits.Collectors;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace AgentLimits;

public partial class MainWindow : Window
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AgentLimits";

    private readonly AppConfig _cfg = AppConfig.Load();
    private readonly ObservableCollection<LimitRow> _rows = [];
    private readonly RowRegistry _registry;
    private PluginHost _plugins = null!;

    private System.Windows.Point? _pressedAt;
    private bool _refreshing;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _autostartItem;
    private Forms.ToolStripMenuItem? _modeItem;
    private DrawIcon? _trayIcon;
    private DateTime? _lastUpdate;
    private HelpWindow? _help;
    private LimitServer? _mcp;
    private readonly SnapshotProvider _snapshot = new();
    private ICollectionView? _rowsView;

    private DragGhost? _ghost;
    private string? _dragSource;
    private int _dragSourceIdx = -1;

    private static readonly TimeSpan MinLoadingTime = TimeSpan.FromMilliseconds(500);
    private const double DragThreshold = 4;

    public MainWindow()
    {
        InitializeComponent();
        _registry = new RowRegistry(_rows);

        LimitRow.ShowUsed = _cfg.ShowUsed;
        UpdateModeText();

        var grouped = new CollectionViewSource { Source = _rows };
        grouped.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LimitRow.Group)));
        _rowsView = grouped.View;
        RowsList.ItemsSource = _rowsView;

        Log.Cleanup();
        Log.Info($"started, keeping logs for {Log.RetentionDays} days");

        _plugins = new PluginHost(_registry, _cfg, Dispatcher);
        _plugins.RegisterBuiltin(new IQuotaSource[]
        {
            BuiltinQuotaSource.Claude(_cfg).First(),
            BuiltinQuotaSource.Zai(_cfg),
            BuiltinQuotaSource.Codex(_cfg),
            BuiltinQuotaSource.Agy(_cfg),
            BuiltinQuotaSource.Minimax(_cfg),
        });
        var discovered = _plugins.DiscoverPlugins();
        Log.Info($"plugins: {_plugins.Sources.Count} sources ({discovered} external)");

        _plugins.AfterRefresh += () =>
        {
            ApplyGroupOrder();
            _lastUpdate = DateTime.Now;
            UpdateStamp();
            UpdateTrayIcon();
            _snapshot.Update(_rows);
            ResizeToContent();
            AutoFitOnFirstLoad();
            DumpState();
        };

        SetupTray();
        _plugins.Start();
        StartMcpServer();

        // Cosmetic tick every 30s: recomputes "resets in Xh Ym" and the peak-hours
        // status (IsPeakActive) without hitting the sources. Without this timer,
        // IsPeakActive freezes forever at its first computed value —
        // PropertyChanged for it never fires from a regular poll cycle.
        var countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        countdownTimer.Tick += (_, _) =>
        {
            foreach (var r in _rows) r.RefreshCountdown();
        };
        countdownTimer.Start();

        Loaded += (_, _) =>
        {
            PlaceWindow();
            if (Environment.GetCommandLineArgs().Contains("--selftest-loading"))
                _ = SelfTestLoadingAsync();
        };
    }

    // ---------- group order ----------

    private void ApplyGroupOrder()
    {
        var order = _cfg.GroupOrder ?? new List<string>();
        var present = _rows.Select(r => r.Group).Distinct().ToList();
        var filtered = order.Where(present.Contains).ToList();
        var missing = present.Where(g => !filtered.Contains(g)).ToList();
        var final = filtered.Concat(missing).ToList();

        var sorted = final
            .SelectMany((g, idx) => _rows.Where(r => r.Group == g)
                                         .Select((r, i) => (Row: r, SortKey: idx * 1000 + i)))
            .OrderBy(t => t.SortKey)
            .Select(t => t.Row)
            .ToList();

        if (sorted.SequenceEqual(_rows)) return;

        for (int i = 0; i < sorted.Count; i++)
        {
            var currentIndex = _rows.IndexOf(sorted[i]);
            if (currentIndex != i) _rows.Move(currentIndex, i);
        }

        // _rows.Move() moves items within groups, but does NOT reorder the
        // groups themselves — CollectionView remembers the order they were
        // created in (first appearance). Refresh() forces groups to rebuild
        // in the current order of _rows.
        _rowsView?.Refresh();
    }

    private void SaveGroupOrder()
    {
        var order = _rows.Select(r => r.Group).Distinct().ToList();
        _cfg.GroupOrder = order;
        try { _cfg.Save(); } catch { }
    }

    // ---------- group drag & drop ----------

    private const string GroupDragFormat = "application/x-agentlimits-group";

    private void GroupHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled) return;
        e.Handled = true;
        var group = GetGroupFromGroupHeader(sender);
        if (group is null) return;

        ApplyGroupOrder();
        var order = _rows.Select(r => r.Group).Distinct().ToList();
        _dragSource = group;
        _dragSourceIdx = order.IndexOf(group);

        _ghost = DragGhost.Create(VisualTreeAncestor(sender, typeof(GroupItem)));
        _ghost?.Show();

        Log.Info($"drag: mouse-down on group='{group}' srcIdx={_dragSourceIdx}");

        try
        {
            var data = new DataObject(GroupDragFormat, group);
            var effect = DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
            Log.Info($"drag: ended, effect={effect}");
        }
        catch (Exception ex)
        {
            Log.Warn($"drag: failed ({ex.Message})");
        }
        finally
        {
            _ghost?.Close();
            _ghost = null;
            _dragSource = null;
            _dragSourceIdx = -1;
        }
    }

    private void Panel_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(GroupDragFormat) is string source && source == _dragSource)
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;
        e.Handled = true;
        _ghost?.FollowCursor();
    }

    private void Panel_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(GroupDragFormat) is not string source || source != _dragSource) { e.Handled = true; return; }

        var pos = e.GetPosition(this);
        var hit = FindGroupUnderCursor(this, pos);
        string? target = hit is null ? null
                                    : (hit.DataContext as System.Windows.Data.CollectionViewGroup)?.Name?.ToString();
        target ??= NearestGroup(e.GetPosition(RowsList));

        Log.Info($"drop: source='{source}' target='{target}' srcIdx={_dragSourceIdx}");
        if (target is null || target == source) { e.Handled = true; return; }

        e.Handled = true;
        MoveGroup(source, target);
    }

    private static string? GetGroupFromGroupHeader(object sender)
    {
        if (sender is not FrameworkElement fe) return null;
        var gi = VisualTreeAncestor(sender, typeof(GroupItem)) as GroupItem;
        if (gi is null) return null;
        if (gi.DataContext is System.Windows.Data.CollectionViewGroup cvg)
            return cvg.Name?.ToString();
        return null;
    }

    private static DependencyObject? VisualTreeAncestor(object start, Type type)
    {
        DependencyObject? d = start as DependencyObject;
        while (d is not null && !type.IsInstanceOfType(d))
            d = VisualTreeHelper.GetParent(d);
        return d;
    }

    private static GroupItem? FindGroupUnderCursor(Visual root, System.Windows.Point pos)
    {
        var hit = VisualTreeHelper.HitTest(root, pos);
        DependencyObject? d = hit?.VisualHit;
        while (d is not null)
        {
            if (d is GroupItem gi) return gi;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    private string? NearestGroup(System.Windows.Point posInList)
    {
        GroupItem? best = null;
        double bestDist = double.MaxValue;
        foreach (var gi in EnumerateGroupItems(RowsList))
        {
            var top = gi.TranslatePoint(new System.Windows.Point(0, 0), RowsList);
            double d;
            if (posInList.Y < top.Y) d = top.Y - posInList.Y;
            else
            {
                double bottom = top.Y + gi.ActualHeight;
                if (posInList.Y > bottom) d = posInList.Y - bottom;
                else d = 0;
            }
            if (d < bestDist) { bestDist = d; best = gi; }
        }
        return (best?.DataContext as System.Windows.Data.CollectionViewGroup)?.Name?.ToString();
    }

    private static IEnumerable<GroupItem> EnumerateGroupItems(Visual root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is GroupItem gi) yield return gi;
            if (c is Visual v)
                foreach (var sub in EnumerateGroupItems(v)) yield return sub;
        }
    }

    private void MoveGroup(string source, string target)
    {
        ApplyGroupOrder();
        var order = _rows.Select(r => r.Group).Distinct().ToList();
        var srcIdx = order.IndexOf(source);
        var tgtIdx = order.IndexOf(target);
        if (srcIdx < 0 || tgtIdx < 0 || srcIdx == tgtIdx) return;

        bool draggedUp = srcIdx > tgtIdx;
        Log.Info($"move: source='{source}' target='{target}' srcIdx={srcIdx} tgtIdx={tgtIdx} up={draggedUp}");

        order.RemoveAt(srcIdx);
        tgtIdx = order.IndexOf(target);
        order.Insert(draggedUp ? tgtIdx : tgtIdx + 1, source);

        _cfg.GroupOrder = order;
        ApplyGroupOrder();
        _rowsView?.Refresh();
        try { _cfg.Save(); } catch (Exception ex) { Log.Warn($"move: save failed ({ex.Message})"); }
    }

    // ---------- positioning ----------

    private const double MarginRight = 18;
    private const double MarginTop = 90;

    private void PlaceWindow()
    {
        if (_cfg.WindowLeft is double l && _cfg.WindowTop is double t && IsOnScreen(l, t))
        {
            Left = l;
            Top = t;
        }
        else
        {
            SnapToCorner();
        }

        // Size: restore from config, otherwise use the default.
        if (_cfg.WindowWidth is double w && w >= MinWidth) Width = w;
        if (_cfg.WindowHeight is double h && h >= MinHeight) Height = h;
    }

    private void SnapToCorner()
    {
        var (right, top) = WorkAreaTopRightDip();
        Left = right - ActualWidth - MarginRight;
        Top = top + MarginTop;
        SavePosition();
    }

    private (double Right, double Top) WorkAreaTopRightDip()
    {
        var wa = Forms.Screen.PrimaryScreen?.WorkingArea
                 ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } ct)
        {
            var p = ct.TransformFromDevice.Transform(new System.Windows.Point(wa.Right, wa.Top));
            return (p.X, p.Y);
        }
        return (wa.Right, wa.Top);
    }

    private bool IsOnScreen(double left, double top)
    {
        var source = PresentationSource.FromVisual(this);
        foreach (var s in Forms.Screen.AllScreens)
        {
            var b = s.WorkingArea;
            double bl = b.Left, bt = b.Top, br = b.Right, bb = b.Bottom;
            if (source?.CompositionTarget is { } ct)
            {
                var tl = ct.TransformFromDevice.Transform(new System.Windows.Point(b.Left, b.Top));
                var brp = ct.TransformFromDevice.Transform(new System.Windows.Point(b.Right, b.Bottom));
                (bl, bt, br, bb) = (tl.X, tl.Y, brp.X, brp.Y);
            }
            if (left >= bl - 50 && left <= br - 50 && top >= bt - 20 && top <= bb - 20)
                return true;
        }
        return false;
    }

    private void Panel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;

        if (e.ClickCount >= 2)
        {
            _pressedAt = null;
            e.Handled = true;
            _ = RefreshWithFeedbackAsync();
            return;
        }

        _pressedAt = e.GetPosition(this);
    }

    private void Panel_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedAt is not System.Windows.Point start || e.LeftButton != MouseButtonState.Pressed) return;

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - start.X) < DragThreshold && Math.Abs(now.Y - start.Y) < DragThreshold) return;

        _pressedAt = null;
        try { DragMove(); } catch { }
        SavePosition();
    }

    private void Panel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => _pressedAt = null;

    private void SavePosition()
    {
        _cfg.WindowLeft = Left;
        _cfg.WindowTop = Top;
        _cfg.WindowWidth = ActualWidth;
        _cfg.WindowHeight = ActualHeight;
        try { _cfg.Save(); } catch { }
    }

    // ---------- tray ----------

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Show / hide", null, (_, _) => ToggleVisible());
        menu.Items.Add("Refresh now", null, (_, _) => { Show(); _ = RefreshWithFeedbackAsync(); });
        menu.Items.Add("Snap to top-right", null, (_, _) => { Show(); SnapToCorner(); });

        _modeItem = new Forms.ToolStripMenuItem("Show used instead of remaining")
        {
            CheckOnClick = true,
            Checked = LimitRow.ShowUsed
        };
        _modeItem.CheckedChanged += (_, _) =>
        {
            if (_modeItem.Checked != LimitRow.ShowUsed) ToggleMode();
        };
        menu.Items.Add(_modeItem);
        menu.Items.Add(new Forms.ToolStripSeparator());

        var approveItem = new Forms.ToolStripMenuItem("Approve new plugins");
        approveItem.Click += (_, _) => ApproveAllPending();
        menu.Items.Add(approveItem);

        var openPluginsItem = new Forms.ToolStripMenuItem("Open plugins folder");
        openPluginsItem.Click += (_, _) => OpenPluginsFolder();
        menu.Items.Add(openPluginsItem);

        var copyGuideItem = new Forms.ToolStripMenuItem("Copy plugin authoring guide");
        copyGuideItem.Click += (_, _) => CopyPluginGuide();
        menu.Items.Add(copyGuideItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        _autostartItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutostartEnabled()
        };
        _autostartItem.CheckedChanged += (_, _) => SetAutostart(_autostartItem.Checked);
        menu.Items.Add(_autostartItem);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var mcpItem = new Forms.ToolStripMenuItem("Copy MCP endpoint");
        mcpItem.Click += (_, _) => CopyMcpEndpoint();
        menu.Items.Add(mcpItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApp());

        _tray = new Forms.NotifyIcon
        {
            Text = "Agent limits",
            Visible = true,
            ContextMenuStrip = menu,
            Icon = _trayIcon = LoadTrayIcon()
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) ToggleVisible();
        };
    }

    private void ApproveAllPending()
    {
        var pending = _plugins.Sources
            .Where(s => !s.IsBuiltin && !s.Approved)
            .Select(s => s.Id)
            .ToList();

        if (pending.Count == 0)
        {
            Log.Info("no plugins awaiting approval");
            return;
        }

        foreach (var id in pending)
        {
            if (!_cfg.ApprovedPlugins.Contains(id))
                _cfg.ApprovedPlugins.Add(id);
        }
        try { _cfg.Save(); } catch (Exception ex) { Log.Warn($"approve save: {ex.Message}"); }

        Log.Info($"approved {pending.Count} plugin(s): {string.Join(", ", pending)}");

        // Flip Approved on the sources themselves (no app restart needed),
        // otherwise the next refresh would still return 'awaiting approval'.
        foreach (var src in _plugins.Sources.Where(s => pending.Contains(s.Id)))
        {
            if (src is ExternalQuotaSource ext) ext.Approved = true;
            _ = _plugins.RefreshOneAsync(src);
        }

        _tray?.ShowBalloonTip(3000, "AgentLimits",
            $"Approved: {string.Join(", ", pending)}", Forms.ToolTipIcon.Info);
    }

    private void OpenPluginsFolder()
    {
        var dir = System.IO.Path.Combine(AppConfig.Dir, _cfg.PluginsDir);
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch (Exception ex) { Log.Warn($"open plugins folder: {ex.Message}"); }
    }

    private void CopyPluginGuide()
    {
        try
        {
            Clipboard.SetText(PluginGuide.Text);
            Log.Info("plugin guide copied to clipboard");
            _tray?.ShowBalloonTip(2000, "AgentLimits",
                "Plugin authoring guide copied. Paste into your AI assistant.",
                Forms.ToolTipIcon.Info);
        }
        catch (Exception ex) { Log.Warn($"copy guide: {ex.Message}"); }
    }

    private void ToggleVisible()
    {
        if (IsVisible)
        {
            Hide();
        }
        else
        {
            Show();
            Topmost = true;
            Activate();
            _ = _plugins.RefreshAllAsync();
        }
    }

    private void Close_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Hide();
    }

    private void Help_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_help is null)
        {
            _help = new HelpWindow { Owner = this };
            _help.Closed += (_, _) => _help = null;
        }
        _help.ShowCentered();
    }

    private void Refresh_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _ = RefreshWithFeedbackAsync();
    }

    private async Task RefreshWithFeedbackAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        Log.Info("manual refresh requested");
        ShowLoading(true);

        var started = DateTime.UtcNow;
        try
        {
            await _plugins.RefreshAllAsync();
        }
        finally
        {
            var left = MinLoadingTime - (DateTime.UtcNow - started);
            if (left > TimeSpan.Zero) await Task.Delay(left);
            ShowLoading(false);
            _refreshing = false;
        }
    }

    private async Task SelfTestLoadingAsync()
    {
        ShowLoading(true);
        await Task.Delay(TimeSpan.FromSeconds(6));
        ShowLoading(false);
    }

    private void ShowLoading(bool on)
    {
        if (on)
        {
            LoadingOverlay.Visibility = Visibility.Visible;
            LoadingOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            ContentBlur.BeginAnimation(BlurEffect.RadiusProperty,
                new DoubleAnimation(0, 7, TimeSpan.FromMilliseconds(120)));
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
            fade.Completed += (_, _) =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            };
            LoadingOverlay.BeginAnimation(OpacityProperty, fade);
            ContentBlur.BeginAnimation(BlurEffect.RadiusProperty,
                new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(160)));
        }
    }

    private void Mode_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ToggleMode();
    }

    private void ToggleMode()
    {
        LimitRow.ShowUsed = !LimitRow.ShowUsed;
        _cfg.ShowUsed = LimitRow.ShowUsed;
        try { _cfg.Save(); } catch { }

        UpdateModeText();
        foreach (var r in _rows) r.RefreshDisplayMode();
        if (_modeItem is not null) _modeItem.Checked = LimitRow.ShowUsed;
    }

    private void UpdateModeText() =>
        ModeButton.ToolTip = LimitRow.ShowUsed
            ? "Showing used — click for remaining"
            : "Showing remaining — click for used";

    protected override void OnClosing(CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }

    private void ExitApp()
    {
        Log.Info("exit");
        SavePosition();
        _plugins?.Stop();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _mcp?.Dispose();
        DestroyIconHandle();
        Application.Current.Shutdown();
    }

    // ---------- MCP server ----------

    private void StartMcpServer()
    {
        if (_cfg.McpPort <= 0) { Log.Info("mcp server: disabled (port=0)"); return; }
        try
        {
            _mcp = new LimitServer(_cfg.McpPort, _snapshot);
            _mcp.Start();
        }
        catch (Exception ex)
        {
            Log.Warn($"mcp server: init failed ({ex.Message})");
            _mcp?.Dispose();
            _mcp = null;
        }
    }

    private void CopyMcpEndpoint()
    {
        if (_mcp is null) { Log.Info("mcp server: not running"); return; }
        try
        {
            Clipboard.SetText(_mcp.Endpoint);
            Log.Info($"mcp server: endpoint copied ({_mcp.Endpoint})");
        }
        catch (Exception ex)
        {
            Log.Warn($"mcp server: clipboard failed ({ex.Message})");
        }
    }

    // ---------- autostart ----------

    private static bool IsAutostartEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) is string s && s.Length > 0;
        }
        catch { return false; }
    }

    private static void SetAutostart(bool enabled)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (exe is not null) k.SetValue(RunValue, $"\"{exe}\"");
            }
            else
            {
                k.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch { }
    }

    // ---------- icon ----------

    private void ResizeToContent()
    {
        UpdateLayout();
        // This used to trigger SizeToContent so WPF would re-measure the height
        // after rows appear/disappear. The window is manually resizable now, so
        // we leave it alone — the user decides how much space they need.
    }

    /// <summary>First launch with no saved size — fit the height to the content.
    /// After that the user decides the size themselves; their choice is saved to WindowHeight.</summary>
    private bool _firstLoadDone;
    private void AutoFitOnFirstLoad()
    {
        if (_firstLoadDone) return;
        if (_cfg.WindowHeight is > 0) { _firstLoadDone = true; return; }
        if (_rows.Count == 0) return;

        // Compute the desired height = content + chrome (border/title bar)
        SizeToContent = SizeToContent.Height;
        UpdateLayout();
        var desired = ActualHeight;
        SizeToContent = SizeToContent.Manual;
        if (desired > 100 && desired < 1500)
        {
            Height = desired;
            _cfg.WindowHeight = desired;
            _cfg.WindowWidth = ActualWidth;
            try { _cfg.Save(); } catch { }
        }
        _firstLoadDone = true;
    }

    private void DumpState()
    {
        try
        {
            var lines = _rows.Select(r =>
                $"{r.Key,-30} hidden={r.Hidden,-5} remaining={(r.RemainingPercent?.ToString("0.##") ?? "null"),-7} error={r.Error ?? "-"}");
            System.IO.File.WriteAllLines(
                System.IO.Path.Combine(AppConfig.Dir, "debug-state.log"),
                new[] { $"# {DateTime.Now:yyyy-MM-dd HH:mm:ss}  window {ActualWidth:0}x{ActualHeight:0}, rows {_rows.Count(r => !r.Hidden)}/{_rows.Count}" }.Concat(lines));
        }
        catch { }
    }

    private void UpdateStamp() =>
        UpdatedText.Text = _lastUpdate is null ? "" : _lastUpdate.Value.ToString("HH:mm");

    private void UpdateTrayIcon()
    {
        if (_tray is null) return;

        // The icon itself is static (Assets/tray.ico — 4 quadrants ok/warn/crit/dead).
        // The color state is conveyed through the tooltip text instead.
        var known = _rows.Where(r => r.RemainingPercent is not null).Select(r => r.RemainingPercent!.Value).ToList();
        var worst = known.Count == 0 ? "no data" : $"lowest {Math.Round(known.Min())}% left";
        _tray.Text = $"Agent limits - {worst}";
    }

    private static DrawIcon BuildIcon(DrawColor color)
    {
        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(DrawColor.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        var handle = bmp.GetHicon();
        using var tmp = DrawIcon.FromHandle(handle);
        var icon = (DrawIcon)tmp.Clone();
        DestroyIconHandle(handle);
        return icon;
    }

    private void DestroyIconHandle()
    {
        DestroyIcon(_trayIcon);
        _trayIcon = null;
    }

    private static void DestroyIcon(DrawIcon? icon)
    {
        try { icon?.Dispose(); } catch { }
    }

    [DllImport("user32.dll", EntryPoint = "DestroyIcon")]
    private static extern bool DestroyIconNative(IntPtr handle);

    private static void DestroyIconHandle(IntPtr handle)
    {
        try { DestroyIconNative(handle); } catch { }
    }

    /// <summary>Loads the icon from Assets/tray.ico. The icon is static — its 4 quadrants
    /// (ok/warn/crit/dead) echo the visual language of the rows in the widget.
    /// The quota's color state is conveyed through the tooltip text.</summary>
    private static DrawIcon LoadTrayIcon()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "tray.ico");
        if (System.IO.File.Exists(path))
            return new DrawIcon(path);

        // Fallback — the old procedurally-drawn icon
        Log.Warn($"tray.ico not found at {path}, using fallback");
        return BuildIcon(DrawColor.FromArgb(0x4A, 0xDE, 0x80));
    }
}