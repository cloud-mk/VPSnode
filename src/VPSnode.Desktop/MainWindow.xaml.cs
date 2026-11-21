using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using VPSnode.Core;

namespace VPSnode.Desktop;

public partial class MainWindow : Window
{
    private readonly Store _store = new();
    private readonly VPSnode.Core.Monitor _monitor;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private TrayIcon? _tray;
    private readonly CancellationTokenSource _app = new();
    private CancellationTokenSource? _current;
    private string _feedbackNode = "";
    private string _nodeFeedback = "";
    private Task? _tick;
    private Task<string>? _manualRun;
    private readonly (string Key, string Label)[] _menu =
        [("overview", "总览"), ("hosts", "VPS"), ("nodes", "代理节点"), ("web", "网站与证书"), ("settings", "设置")];
    private string _page = "overview";
    private string _hostId = "";
    private string _nodeId = "";
    private string _nodeHostFilter = "";
    private string _search = "";
    private bool _anomalyOnly;
    private bool _config;
    private bool _closing;
    private bool _webCertificates;
    private int _historyDays = 1;
    private int _intervalMinutes = 15;
    private int _configurationTab;
    private readonly SqliteCommand _changes;
    private long _renderVersion = -1;
    private long _noticeVersion = -1;

    public MainWindow()
    {
        InitializeComponent();
        var version = typeof(Store).Assembly.GetName().Version!.ToString(3);
        Title = $"VPSnode {version}";
        BrandVersion.Text = $"v{version}";
        Width = Math.Min(Width, Math.Max(MinWidth, SystemParameters.WorkArea.Width - 16));
        Height = Math.Min(Height, Math.Max(MinHeight, SystemParameters.WorkArea.Height - 16));
        _monitor = new(_store);
        _changes = _store.WatchChanges();
        TasksButton.Click += (_, _) => Go("tasks");
        StopTask.Click += (_, _) => _monitor.CancelActive();
        _recentTask = TaskHistory(1).FirstOrDefault();
        UpdateTaskPanel();
        _monitor.Changed += TaskChanged;
        Ui.Theme(_store.Settings.Theme);
        SourceInitialized += (_, _) =>
        {
            var source = (System.Windows.Interop.HwndSource)System.Windows.PresentationSource.FromVisual(this)!;
            _tray = new TrayIcon(source, _store.DirectoryPath, ShowWindow, Quit);
        };
        Closed += (_, _) => { _tray?.Dispose(); _nodeColumns.Detach(); _monitor.Changed -= TaskChanged; };
        Closing += WindowClosing;
        _timer.Tick += TimerTick;
        _timer.Start();
        Render();
    }

    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closing) return;
        e.Cancel = true;
        Hide();
    }

    internal void ShowWindow()
    {
        if (_closing) return;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        RefreshIfChanged();
    }

    private async void Quit()
    {
        if (_closing) return;
        _closing = true;
        _tray?.Dispose();
        _timer.Stop();
        _app.Cancel();
        _current?.Cancel();
        if (_tick != null)
        {
            try { await _tick; } catch (OperationCanceledException) { } catch (Exception) { }
        }
        if (_manualRun != null)
        {
            try { await _manualRun; } catch (OperationCanceledException) { }
        }
        _changes.Connection?.Dispose();
        _changes.Dispose();
        _monitor.Changed -= TaskChanged;
        _nodeColumns.Detach();
        Application.Current.Shutdown();
    }

    private async void TimerTick(object? sender, EventArgs e)
    {
        if (_closing || _tick is { IsCompleted: false }) return;
        _tick = Task.Run(() => _monitor.Tick(_app.Token), _app.Token);
        try
        {
            await _tick;
            if (_closing) return;
            var version = Convert.ToInt64(_changes.ExecuteScalar());
            if (_noticeVersion != version)
            {
                ShowNotices();
                _noticeVersion = version;
            }
            if (IsVisible) RefreshIfChanged();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Status("巡检暂缓，稍后重试。"); }
    }

    private void ShowNotices()
    {
        foreach (var item in _store.Latest().Values.Where(x => x.Kind == "tls" && x.Expires.HasValue &&
            x.State is "即将到期" or "已过期"))
        {
            var remain = (item.Expires!.Value - DateTimeOffset.Now).TotalDays;
            var threshold = new[] { 0, 3, 7, 14, 30 }.FirstOrDefault(x => remain <= x);
            var key = $"tls|{item.Id}|{item.Expires.Value.UtcDateTime:O}|{threshold}";
            if (!_store.MarkNotice(key)) continue;
            var website = WebProbe.Targets(_store).FirstOrDefault(x => x.Id == item.Id);
            if (website != null) _tray?.Notice("VPSnode · 证书提醒",
                $"{website.Name} · {website.Location} · {item.State}");
        }
    }

    private void RefreshIfChanged()
    {
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox { SelectionLength: > 0 } || System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Pressed) return;
        if (_page == "settings" || OwnedWindows.OfType<Window>().Any(window => window.IsVisible)) return;
        var version = Convert.ToInt64(_changes.ExecuteScalar());
        if (_renderVersion == version) return;
        _renderVersion = version;
        var offset = Scroller.VerticalOffset;
        RefreshVisibleData();
        Scroller.ScrollToVerticalOffset(offset);
    }

    private async void Run(string kind, string id = "")
    {
        if (_current != null) { Status("已有手动任务运行，可先取消。"); return; }
        using var source = CancellationTokenSource.CreateLinkedTokenSource(_app.Token);
        _current = source;
        var label = kind == "speed" ? "下载测速" : "真实请求延迟";
        var nodeAction = kind is "speed" or "node";
        void Feedback(string message)
        {
            if (nodeAction) { _feedbackNode = id; _nodeFeedback = $"{label} · {message}"; }
            Status(nodeAction ? _nodeFeedback : message);
        }
        Feedback("等待执行");
        RefreshVisibleData();
        try
        {
            void Started() => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_current != source || _closing || !nodeAction) return;
                Feedback("正在执行…");
                if (_page == "node" && _nodeId == id) Render();
            }));
            _manualRun = Task.Run(() => _monitor.Run(kind, id, source.Token, Started));
            Feedback(await _manualRun);
        }
        catch (OperationCanceledException) { Feedback("已取消"); }
        catch (Exception ex) { Feedback("执行失败"); Fail(ex); }
        finally
        {
            _manualRun = null;
            _current = null;
            if (!_closing) RefreshVisibleData();
        }
    }

    private void Status(string label) => StatusLine.Text = "●  " + DisplayFormat.Details(label);
    private void Fail(Exception ex)
    {
        var text = ex is InvalidDataException or TimeoutException ? ex.Message : "操作未完成，请检查本机设置或读取权限。";
        Status(text);
        if (IsVisible && !_closing) MessageBox.Show(this, text, "VPSnode", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private readonly Stack<(string Page, string Host, string Node, string Filter, string Search, bool Config, bool Certs)> _history = new();

    private void Back()
    {
        if (!_history.TryPop(out var last)) return;
        (_page, _hostId, _nodeId, _nodeHostFilter, _search, _config, _webCertificates) = last;
        Render();
        Scroller.ScrollToTop();
    }

    private void Go(string page) => Navigate(page, null);

    private void Navigate(string page, Action? select)
    {
        if (_page != page) _history.Push((_page, _hostId, _nodeId, _nodeHostFilter, _search, _config, _webCertificates));
        select?.Invoke();
        _page = page;
        Render();
        Scroller.ScrollToTop();
        Scroller.ScrollToLeftEnd();
    }

    private void OpenHost(string id) => Navigate("host", () => _hostId = id);
    private void OpenNode(string id) => Navigate("node", () => { _nodeId = id; _config = false; _configurationTab = 0; });
    private void HostNodes(string id) => Navigate("nodes", () => { _nodeHostFilter = id; _search = ""; });

    private void Render()
    {
        _nodeColumns.Detach();
        _nodeRows.Clear();
        _nodeSummaries.Clear();
        _nodeCancel = null;
        _intervalMinutes = _store.Settings.IntervalMinutes;
        UpdateTaskPanel();
        Navigation.Children.Clear();
        foreach (var (key, name) in _menu)
        {
            var active = _page == key || (_page == "host" && key == "hosts") || (_page == "node" && key == "nodes");
            var button = Ui.Button(name, () => Go(key), active);
            button.Margin = new Thickness(0, 0, 0, 6);
            Navigation.Children.Add(button);
        }
        PageFrame.MinWidth = 620;
        PageBody.Content = _page switch
        {
            "hosts" => HostsPage(),
            "host" => HostPage(),
            "nodes" => NodesPage(),
            "node" => NodePage(),
            "web" => WebPage(),
            "tasks" => TasksPage(),
            "settings" => SettingsPage(),
            _ => OverviewPage()
        };
    }

    private StackPanel Header(string title, params UIElement[] buttons)
    {
        var text = Ui.Value(title, 19, bold: true);
        text.Margin = new Thickness(0, 0, 16, 3);
        var row = Ui.Row();
        if (_history.Count > 0) row.Children.Add(Ui.Button("返回", Back));
        row.Children.Add(text);
        foreach (var button in buttons) row.Children.Add(button);
        if (_current != null && _page != "nodes") row.Children.Add(Ui.Button("取消任务", () => _current?.Cancel()));
        row.Margin = new Thickness(0, 0, 0, 9);
        return Ui.Stack(row);
    }

    private static string Time(Observation? value) => value?.At.ToLocalTime().ToString("MM-dd HH:mm") ?? "—";
    private static string Number(Observation? value, string unit) =>
        value?.Value is double number ? (unit == "ms" ? DisplayFormat.Latency(number) : $"{number:0.#} {unit}") : "—";
    private static string HostName(Host? host) => host is null ? "未关联" :
        string.IsNullOrEmpty(host.Hostname) ? host.Name : host.Hostname;
    private static string Field(string first, string second) => string.IsNullOrWhiteSpace(first) ? second : first;
    private static bool IsPaused(Node n) => n.Paused || n.SourceRemoved && !n.VpsVerified && !n.Manual;
    private static bool IsPanelDisabled(Node n) =>
        n.Server.GetValueOrDefault("启用") == "否" || n.Server.GetValueOrDefault("客户端启用") == "否";
    private bool IsCurrent(Observation? value, string state) => value?.State == state && !IsStale(value);
    private bool IsNodeAvailable(Node n, Observation? value) =>
        !IsPaused(n) && !IsPanelDisabled(n) && IsCurrent(value, "可用");

    private string NodeState(Node n, Observation? latest)
    {
        if (IsPaused(n)) return "Ⅱ 已暂停";
        if (IsPanelDisabled(n)) return n.Server.GetValueOrDefault("启用") == "否" ? "Ⅱ 入站已禁用" : "Ⅱ 客户端已禁用";
        if (n.Link.Length == 0 && latest is null) return "○ 待补全";
        return State(latest);
    }

    private bool IsStale(Observation value)
    {
        var age = value.Kind == "tls" ? TimeSpan.FromDays(2) : TimeSpan.FromMinutes(_intervalMinutes * 2d + 1);
        return value.Kind is "host" or "node" or "web80" or "web443" or "tls" && DateTimeOffset.Now - value.At > age;
    }

    private string State(Observation? value)
    {
        if (value is null) return "○ 待检测";
        var name = value.State;
        var state = name switch
        {
            "SSH 可登录" or "正常" or "可用" or "完成" or "TLS 正常" or "解析正常" or "有网页" or "跳转后有网页" => "✓ " + name,
            "即将到期" or "未检测" or "依赖缺失" or "参数待验证" or "待补全" => "◇ " + name,
            _ => "! " + name
        };
        return IsStale(value) ? (state.StartsWith("✓", StringComparison.Ordinal) ? "◇ 上次" + name : state) + " · 已过时" : state;
    }

    private static ComboBox Choice(object[] values, object? selected, Action<int> change)
    {
        var options = new ComboBox { ItemsSource = values, MinWidth = 125, Padding = new Thickness(7, 5, 7, 5),
            Margin = new Thickness(0, 0, 8, 0), SelectedItem = selected };
        options.SelectionChanged += (_, _) => { if (options.SelectedIndex >= 0) change(options.SelectedIndex); };
        return options;
    }

    private readonly record struct NodeRow(Node Node, string Host, string Name, string Protocol, string Port,
        string Status, string Delay, string Recent, double Speed = 0, string SpeedTip = "尚未测速", string TaskState = "", bool CanTest = true, double? DelayValue = null)
    {
        public string SpeedText => SpeedTip == "尚未测速" ? "未测速" : $"{Speed:0.#} Mbps";
        public Visibility SpeedVisibility => SpeedTip == "尚未测速" ? Visibility.Collapsed : Visibility.Visible;
    }
    private readonly record struct HostRow(Host Host, string Name, string Hostname, string Address, string Status,
        string Recent, int Count);
    private readonly record struct WebRow(WebTarget Target, string DomainPort, string Vps, string Ip, string Fallback,
        string Website, string On80, string On443, string Where, string Cert, string Expires, string Problem);

    private NodeRow NodeItem(Node n, Host? host, Observation? last) => new(n, HostName(host), n.Name,
        string.Join(" · ", new[] { n.Protocol, n.Transport, n.Security }.Where(x => x.Length != 0)),
        n.Port == 0 ? "—" : n.Port.ToString(), NodeState(n, last), IsPaused(n) || IsPanelDisabled(n) || last?.State != "可用" ? "—" : Number(last, "ms"), Time(last), CanTest: _current == null && _runningTask == null, DelayValue: last?.State == "可用" ? last.Value : null);

    private StackPanel OverviewPage()
    {
        var data = _store.Latest();
        var hosts = _store.Hosts.OrderBy(h => h.Name).ToArray();
        var nodes = _store.Nodes;
        var websites = WebProbe.Targets(_store);
        var ok = hosts.Count(h => IsCurrent(data.GetValueOrDefault(("host", h.Id)), "SSH 可登录"));
        var nodeOk = nodes.Count(n => IsNodeAvailable(n, data.GetValueOrDefault(("node", n.Id))));
        var failed = websites.Count(w => w.Enabled && w.CheckWebsite && new[] { "web80", "web443" }
            .Any(k => data.TryGetValue((k, w.Id), out var check) && check.State is not ("有网页" or "跳转后有网页")));
        var expiring = websites.Count(w => data.GetValueOrDefault(("tls", w.Id))?.State is "即将到期" or "已过期");
        Button Tile(string label, string icon, int count, int total, string dest, bool alert = false, bool certificates = false)
        {
            var button = Ui.Button(label, () => Navigate(dest, () => { if (dest == "web") _webCertificates = certificates; }));
            button.Content = Ui.Row(Ui.Text(label + "  ", 13, "Quiet"), Ui.Text(alert ? count.ToString() : $"{count} / {total}", 17, alert && count > 0 ? "Warn" : "Text", true));
            System.Windows.Automation.AutomationProperties.SetName(button, label);
            button.ToolTip = $"{label} {count}，共 {total} 项";
            button.Margin = new Thickness(0, 0, 0, 6);
            return button;
        }
        var root = Ui.Stack(Header("总览", Ui.Button("立即巡检", () => Run("all"), true)),
            Ui.Columns(Tile("VPS 可登录", "\uE950", ok, hosts.Length, "hosts"), Tile("节点可用", "\uE839", nodeOk, nodes.Length, "nodes"),
                Tile("网站异常", "\uE774", failed, websites.Length, "web", true), Tile("证书提醒", "\uE8D7", expiring, websites.Length, "web", true, true)));
        var filter = new CheckBox { Content = "仅显示异常的 VPS 和节点", IsChecked = _anomalyOnly };
        void ApplyFilter(object sender, RoutedEventArgs args) { _anomalyOnly = filter.IsChecked == true; Render(); }
        filter.Checked += ApplyFilter;
        filter.Unchecked += ApplyFilter;
        root.Children.Add(filter);
        foreach (var host in hosts)
        {
            var state = data.GetValueOrDefault(("host", host.Id));
            var grouped = nodes.Where(n => n.HostId == host.Id).Select(n => NodeItem(n, host, data.GetValueOrDefault(("node", n.Id)))).ToArray();
            if (_anomalyOnly && IsCurrent(state, "SSH 可登录") && grouped.All(x => IsNodeAvailable(x.Node, data.GetValueOrDefault(("node", x.Node.Id))))) continue;
            var rows = grouped.Where(n => !_anomalyOnly || !IsNodeAvailable(n.Node, data.GetValueOrDefault(("node", n.Node.Id)))).Take(8).ToArray();
            var card = Ui.Stack(Ui.Row(Ui.Value(host.Name, 15, true), Ui.Button("详情", () => OpenHost(host.Id)), new StatusPill { Value = State(state) }),
                Ui.Value($"{host.Address} · {grouped.Length} 个节点 · {Time(state)}", 13));
            if (rows.Length > 0)
            {
                var table = Ui.Table(rows,
                    [("节点", "Name", -1), ("当前状态", "Status", 135), ("最近延迟", "Delay", 105), ("检测时间", "Recent", 116)],
                    ("详情", row => OpenNode(row.Node.Id)));
                var groupBrush = Array.IndexOf(hosts, host) % 2 == 0 ? "Surface" : "GroupAlt";
                Ui.Brush(table, DataGrid.RowBackgroundProperty, groupBrush);
                Ui.Brush(table, DataGrid.AlternatingRowBackgroundProperty, groupBrush);
                card.Children.Add(table);
                card.Children.Add(Ui.Button("查看此 VPS 的全部节点", () => HostNodes(host.Id)));
            }
            var groupCard = Ui.Card(card);
            if (Array.IndexOf(hosts, host) % 2 == 1) Ui.Brush(groupCard, Border.BackgroundProperty, "GroupAlt");
            root.Children.Add(groupCard);
        }
        if (hosts.Length == 0)
            root.Children.Add(Ui.Card(Ui.Stack(Ui.Section("还没有 VPS"), Ui.Row(
                Ui.Button("添加 VPS", AddHost, true),
                Ui.Button("设置 v2rayN", () => Go("settings")),
                Ui.Button("导入资产 JSON", ImportInventory)))));
        return root;
    }
}
