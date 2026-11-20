using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using VPSnode.Core;

namespace VPSnode.Desktop;

public partial class MainWindow
{
    private async void RemoveAsset(string kind, string id, string name)
    {
        var effect = kind switch {
            "host" => $"保留 {_store.Nodes.Count(n => n.HostId == id)} 个节点和 {_store.Websites.Count(w => w.HostId == id)} 个已登记网站，转为未归属。",
            "node" => $"解除与 {WebProbe.Targets(_store).Count(w => w.FallbackNodeIds.Contains(id) || w.CertificateNodeIds.Contains(id))} 个网站或证书端点的关联。以后盘点或同步来源时可能重新导入。",
            _ => "同时移除这个端点的网站与证书检测。"
        };
        if (MessageBox.Show(this, $"从本地台账删除「{name}」及其检测历史？\n{effect}\n远端服务器与 v2rayN 配置保留。",
            "删除本地记录", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            await _monitor.Delete(kind, id, _app.Token);
            Go(kind == "host" ? "hosts" : kind == "node" ? "nodes" : "web");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Fail(ex); }
    }

    private StackPanel HostsPage()
    {
        var latest = _store.Latest();
        var nodes = _store.Nodes;
        var rows = _store.Hosts.OrderBy(h => h.Name).Select(h =>
        {
            var result = latest.GetValueOrDefault(("host", h.Id));
            return new HostRow(h, h.Name, HostName(h), Field(h.Address, h.Ipv6), State(result), Time(result),
                nodes.Count(n => n.HostId == h.Id));
        }).ToArray();
        var table = Ui.Table(rows, [("名称", "Name", -1), ("主机名", "Hostname", -1), ("地址", "Address", -1),
            ("SSH / 状态", "Status", 135), ("最近检测", "Recent", 105)], ("主机详情", row => OpenHost(row.Host.Id)));
        var count = new FrameworkElementFactory(typeof(Button));
        var number = new FrameworkElementFactory(typeof(TextBlock));
        count.SetBinding(ContentControl.ContentProperty, new System.Windows.Data.Binding("Count"));
        number.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
        number.SetValue(TextBlock.TextDecorationsProperty, TextDecorations.Underline);
        count.SetValue(ContentControl.ContentTemplateProperty, new DataTemplate { VisualTree = number });
        count.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        count.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        count.SetValue(Control.FontSizeProperty, 17d);
        count.SetValue(Control.FontWeightProperty, FontWeights.Bold);
        count.SetValue(Control.PaddingProperty, new Thickness(5, 1, 5, 1));
        count.SetValue(ToolTipProperty, "查看此 VPS 的节点");
        count.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Button { DataContext: HostRow item }) HostNodes(item.Host.Id);
        }));
        table.Columns.Insert(4, new DataGridTemplateColumn { Header = "节点", Width = 52, CellTemplate = new DataTemplate { VisualTree = count } });
        return Ui.Stack(Header("VPS", Ui.Button("添加 VPS", AddHost, true), Ui.Button("从 SSH 别名导入", ImportAlias),
            Ui.Button("导入资产 JSON", ImportInventory)), Ui.Card(table));
    }

    private StackPanel HostPage()
    {
        var host = _store.Hosts.FirstOrDefault(h => h.Id == _hostId);
        if (host is null) return HostsPage();
        var recent = _store.Latest();
        var result = recent.GetValueOrDefault(("host", host.Id));
        var path = Field(host.PanelPath, host.RemotePanelPath);
        var panel = host.TunnelPort.HasValue && path.Length > 0
            ? $"{Field(host.PanelScheme, "http")}://127.0.0.1:{host.TunnelPort}/{path.TrimStart('/')}" : "";
        var nodes = _store.Nodes.Where(x => x.HostId == host.Id).ToArray();
        var ports = new PortPlan(host, nodes, _store.Websites);
        var card = Ui.Stack(Ui.Section("3x-ui 面板"),
            Fields([new("服务器面板端口", host.RemotePanelPort?.ToString() ?? "尚未采集", false), new("服务器面板路径", host.RemotePanelPath, false),
                new("本机隧道端口", host.TunnelPort?.ToString() ?? "可在编辑中填写已有隧道端口", false), new("路径覆盖", host.PanelPath, false),
                new("本机地址", panel, false)]));

        var stack = Ui.Stack(Header(host.Name, Ui.Button("一键刷新", () => Run("inventory", host.Id), true), Ui.Button("编辑或填写备注", () => EditHost(host)), Ui.Button("删除", () => RemoveAsset("host", host.Id, host.Name)),
            Ui.Button($"查看 {nodes.Length} 个节点", () => HostNodes(host.Id))),
            Ui.Columns(Ui.Card(Ui.Stack(Ui.Section("主机信息"), Ui.Value($"SSH {State(result)} · {Time(result)} {result?.Detail}", 13), Fields([
                new("主机名", host.Hostname, false), new("公网 IPv4 或地址", host.Address, false), new("IPv6", host.Ipv6, false),
                new("硬件", host.Hardware, false), new("系统", host.Os, false), new("SSH 别名", host.SshAlias, false),
                new("登录账号", host.User, false), new("SSH 端口", host.SshPort.ToString(), false),
                new("密钥位置", host.KeyPath, false)]))), Ui.Card(Ui.Stack(Ui.Section("已安装节点"), Ui.Value($"最近盘点 {host.InventoriedAt?.ToLocalTime().ToString("MM-dd HH:mm") ?? "尚未盘点"} · {nodes.Length} 条", 13), Ui.Table(
                nodes.Select(n => NodeItem(n, host, recent.GetValueOrDefault(("node", n.Id)))).ToArray(),
                [("节点", "Name", -1), ("状态", "Status", -1)], ("详情", row => OpenNode(row.Node.Id)))))),
            PortCard(ports), Ui.Columns(Ui.Card(card), Ui.Card(Ui.Stack(Ui.Section("备注"), Ui.Value(Field(host.Notes, "尚无备注"))))));
        var websites = WebProbe.Targets(_store).Where(w => w.HostId == host.Id).ToArray();
        if (websites.Length > 0)
        {
            var links = Ui.Row();
            foreach (var w in websites) links.Children.Add(Ui.Button($"{WebProbe.Domain(w)}:{w.Port} · {w.Location}", () => WebsiteResults(w)));
            stack.Children.Add(Ui.Card(Ui.Stack(Ui.Section("该 VPS 的网站与证书"), links)));
        }
        return stack;
    }

    private static Border PortCard(PortPlan plan)
    {
        var body = Ui.Stack(Ui.Section("部署端口"), Ui.Text("本次盘点未占用的常用端口", brush: "Quiet", bold: true));
        var choices = Ui.Row();
        foreach (var value in plan.Candidates)
            choices.Children.Add(new StatusPill { Value = "✓ " + value });
        body.Children.Add(plan.Candidates.Length > 0 ? choices : Ui.Text(plan.HasSnapshot ?
            "常用候选端口均有监听或登记。" : "刷新主机后查看候选端口。", brush: "Quiet"));
        body.Children.Add(Ui.Text("部署前请复查监听、端口转发和防火墙规则。", 12, "Quiet"));
        var heading = Ui.Section("已监听或已登记");
        heading.Margin = new Thickness(0, 16, 0, 6);
        body.Children.Add(heading);
        static DataGrid PortTable(PortUse[] entries) => Ui.Table(entries.Select(p => new { Endpoint = $"{p.Protocol} {p.Port}", p.Purpose,
                ListenerState = (p.State == "已监听" ? "● " : "◇ ") + p.State, p.Addresses }),
            [("端口", "Endpoint", 105), ("节点 / 面板 / 进程", "Purpose", -2),
             ("盘点状态", "ListenerState", 160), ("绑定地址", "Addresses", -1)]);
        body.Children.Add(PortTable(plan.Used));
        if (plan.Unidentified.Length > 0)
            body.Children.Add(new Expander { Header = $"其他未识别监听（{plan.Unidentified.Length}）", Content = PortTable(plan.Unidentified) });
        return Ui.Card(body);
    }

    private StackPanel NodesPage()
    {
        var hosts = _store.Hosts.OrderBy(h => h.Name).ToArray();
        var hostIndex = hosts.ToDictionary(x => x.Id);
        var last = _store.Latest();
        var root = Ui.Stack();
        var choices = new[] { "全部 VPS", "未归属" }.Concat(hosts.Select(x => x.Name)).Cast<object>().ToArray();
        var index = _nodeHostFilter == "-" ? 1 : Array.FindIndex(hosts, h => h.Id == _nodeHostFilter) + 2;
        if (index == 1 && _nodeHostFilter != "-") index = 0;
        var selector = Choice(choices, choices[index], i =>
        {
            _nodeHostFilter = i == 0 ? "" : i == 1 ? "-" : hosts[i - 2].Id;
            Render();
        });
        var search = new TextBox { Text = _search, Width = 190, Margin = new Thickness(8, 0, 8, 0) };
        System.Windows.Automation.AutomationProperties.SetName(search, "搜索节点");
        var options = Ui.Row(Ui.Text("所属 VPS  "), selector, search, Ui.Button("搜索", () => { _search = search.Text.Trim(); Render(); }),
            Ui.Button("清除", () => { _search = ""; Render(); }));
        search.KeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            _search = search.Text.Trim();
            Render();
        };
        var selected = hostIndex.GetValueOrDefault(_nodeHostFilter);
        root.Children.Add(Header("代理节点", Ui.Button("粘贴分享链接", () => ImportLinks(false), true),
            Ui.Button("导入 TXT", () => ImportLinks(true)), Ui.Button("v2rayN 同步", SyncDialog),
            Ui.Button("核对重复节点", DuplicateNodes)));
        root.Children.Add(Ui.Row(Ui.Button("全测延迟", () => Run("nodes")),
            Ui.Button("全测下载速度", BatchSpeed), Ui.Text("全测覆盖全部 VPS 与未归属节点；下载串行、每节点限时 45 秒。", 12, "Quiet")));
        if (_nodeHostFilter.Length > 0) options.Children.Add(Ui.Button("本组测延迟", () => Run("host-nodes", selected?.Id ?? "")));
        options.Margin = new Thickness(0, 0, 0, 9);
        root.Children.Add(options);
        var matching = _store.Nodes.Where(n => (_nodeHostFilter.Length == 0 || n.HostId == _nodeHostFilter ||
            (_nodeHostFilter == "-" && n.HostId.Length == 0)) &&
            (_search.Length == 0 || (n.Name + " " + n.Address + " " + n.Protocol).Contains(_search, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(n => n.Name).ToArray();
        var speeds = _store.LatestDownloads();
        var maximum = Math.Max(1, speeds.Values.Select(o => o.Value ?? 0).DefaultIfEmpty().Max());
        int groupIndex = 0;
        foreach (var group in matching.GroupBy(n => n.HostId).OrderBy(g => HostName(hostIndex.GetValueOrDefault(g.Key))))
        {
            var rows = group.Select(n => NodeItem(n, hostIndex.GetValueOrDefault(n.HostId), last.GetValueOrDefault(("node", n.Id)))).ToArray();
            for (int i = 0; i < rows.Length; i++)
                if (speeds.TryGetValue(rows[i].Node.Id, out var observation) && observation.Value is double rate)
                    rows[i] = rows[i] with { Speed = rate, SpeedTip = $"{rate:0.#} Mbps · {Time(observation)}" };
            var progress = _runningTask?.Items.Where(i => i.Kind is "node" or "speed").GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Last());
            if (progress != null)
                for (int i = 0; i < rows.Length; i++)
                    if (progress.TryGetValue(rows[i].Node.Id, out var taskItem))
                        rows[i] = rows[i] with { TaskState = taskItem.State };
            var title = $"{HostName(hostIndex.GetValueOrDefault(group.Key))} · {rows.Length} 个节点";
            var ok = rows.Count(r => IsNodeAvailable(r.Node, last.GetValueOrDefault(("node", r.Node.Id))));
            var paused = rows.Count(r => IsPaused(r.Node));
            var row = Ui.Table(rows, [("节点 / 下载", "Name", -1.3), ("协议 / 传输", "Protocol", -1.5),
                ("端口", "Port", 68), ("状态", "Status", -1), ("最近延迟", "Delay", 100), ("最近检测", "Recent", 105)],
                ("详情", item => OpenNode(item.Node.Id)), ("测延迟", item => Run("node", item.Node.Id)), ("测下载", item => Run("speed", item.Node.Id)));

            row.Columns[0] = Ui.NavigationColumn("节点 / 最近下载", "Name", "SpeedText", -1.5, 160, maximum);
            row.Columns[1] = Ui.NavigationColumn("协议 / 传输", "Protocol", "", -1.2, 168);
            row.Columns[2] = Ui.NavigationColumn("端口", "Port", "", 64, 64);
            row.Columns[3] = Ui.NavigationColumn("状态", "Status", progress != null ? "TaskState" : "", -1, 116);
            row.Columns[^1].Width = new DataGridLength(218);
            row.Columns[^1].MinWidth = 218;
            // Keep Details accessible; Run itself guards concurrent submissions.
            row.LoadingRow += (_, e) => e.Row.ToolTip = "拖选文字可复制；双击文字或行内空白查看详情";
            row.RowHeight = double.NaN;
            row.MinRowHeight = 36;
            var rowStyle = new Style(typeof(DataGridRow));
            rowStyle.Setters.Add(new Setter(ToolTipProperty, new System.Windows.Data.Binding("SpeedTip")));
            var groupBrush = groupIndex % 2 == 0 ? "Surface" : "GroupAlt";
            Ui.Brush(row, DataGrid.RowBackgroundProperty, groupBrush);
            Ui.Brush(row, DataGrid.AlternatingRowBackgroundProperty, groupBrush);
            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("MutedSurface")));
            rowStyle.Triggers.Add(hover);
            row.RowStyle = rowStyle;
            row.PreviewMouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2) return;
                NavigateFromRow(e);
            };
            void NavigateFromRow(System.Windows.Input.MouseButtonEventArgs e)
            {
                for (var source = e.OriginalSource as DependencyObject; source is Visual; source = VisualTreeHelper.GetParent(source))
                {
                    if (source is System.Windows.Controls.Primitives.ButtonBase) return;
                    if (source is DataGridRow { Item: NodeRow item })
                    { e.Handled = true; Dispatcher.BeginInvoke(new Action(() => OpenNode(item.Node.Id))); return; }
                }
            }
            var groupCard = Ui.Card(Ui.Stack(Ui.Value(title, 16, true),
                Ui.Text($"{ok} 可用 · {paused} 已暂停", brush: "Quiet"), row));
            if (groupIndex++ % 2 == 1) { Ui.Brush(groupCard, Border.BackgroundProperty, "GroupAlt"); Ui.Brush(row, DataGrid.RowBackgroundProperty, "GroupAlt"); }
            root.Children.Add(groupCard);
        }
        if (matching.Length == 0) root.Children.Add(Ui.Card(Ui.Text("没有符合当前条件的节点。可导入分享链接、同步 v2rayN，或在 VPS 中执行一键刷新。")));
        return root;
    }

    private StackPanel NodePage()
    {
        var n = _store.Nodes.FirstOrDefault(x => x.Id == _nodeId);
        if (n is null) return NodesPage();
        var latest = _store.Latest();
        var ping = latest.GetValueOrDefault(("node", n.Id));
        var speed = latest.GetValueOrDefault(("speed", n.Id));
        var pingButton = Ui.Button("立即检测", () => Run("node", n.Id));
        var speedButton = Ui.Button("测下载速度", () => Run("speed", n.Id), true);
        pingButton.IsEnabled = speedButton.IsEnabled = _current is null;
        var actions = Header(n.Name, pingButton, speedButton,
            Ui.Button(_config ? "趋势" : "配置详情", () => { _config = !_config; Render(); }),
            Ui.Button("分享", () => Share(n)), Ui.Button("编辑", () => EditNode(n)), Ui.Button("删除", () => RemoveAsset("node", n.Id, n.Name)));
        var root = Ui.Stack(actions, Ui.Row(Ui.Text(NodeState(n, ping), brush: "Quiet"),
            Ui.Value(Time(ping) + (n.SourceRemoved ? "  ·  v2rayN 来源已删除" : "  ·  " + DisplayFormat.Details(ping?.Detail ?? "")), 13)));
        if (_feedbackNode == n.Id) root.Children.Add(Ui.Text(DisplayFormat.Details(_nodeFeedback), bold: true));
        if (speed != null) root.Children.Add(Ui.Text($"最近测速 {speed.At.ToLocalTime():HH:mm:ss} · {speed.State} · {speed.Detail}", brush: "Quiet"));
        var relations = Ui.Row();
        var owner = _store.Hosts.FirstOrDefault(h => h.Id == n.HostId);
        if (owner != null) relations.Children.Add(Ui.Button($"主机 · {owner.Name}", () => OpenHost(owner.Id)));
        foreach (var site in WebProbe.Targets(_store).Where(w => WebProbe.IsRelated(n, w)))
            relations.Children.Add(Ui.Button($"{WebProbe.Domain(site)}:{site.Port} · {site.Location}", () => WebsiteResults(site)));
        if (relations.Children.Count > 0) root.Children.Add(relations);
        if (_config)
        {
            root.Children.Add(Ui.Button("导出节点配置…", () => ExportNode(n)));
            root.Children.Add(ConfigTabs(n));
            if (n.Security == "reality") root.Children.Add(Reality(n, latest));
            return root;
        }
        var end = DateTimeOffset.Now;
        var history = _store.History("node", n.Id, end.AddDays(-_historyDays)).Reverse().ToArray();
        var downloads = _store.History("speed", n.Id, end.AddDays(-_store.Settings.HistoryDays), 100)
            .Where(x => x.Value.HasValue && x.State == "完成").Take(12).Reverse().ToArray();
        Border Stat(string name, string text, string note) =>
            Ui.Card(Ui.Stack(Ui.Text(name, brush: "Quiet"), Ui.Value(text, 22, bold: true), Ui.Value(note, 13)));
        root.Children.Add(Ui.Columns(Ui.Card(Ui.Stack(Ui.Text("最近延迟", brush: "Quiet"), new LatencyBadge { Milliseconds = ping?.Value, Value = ping?.State == "可用" ? Number(ping, "ms") : "—" }, Ui.Value(Time(ping), 13))), Stat("最近手动下载",
            speed is null ? "尚未测速" : speed.Value.HasValue ? Number(speed, "Mbps") : speed.State, Time(speed)), Stat("最近检测", Time(ping), n.Protocol + " · " + n.Security)));
        var peers = _store.Nodes.Where(peer => peer.Id != n.Id && peer.HostId != n.HostId &&
            peer.Protocol == n.Protocol && peer.Security == n.Security &&
            (peer.Transport == n.Transport || (peer.Transport is "tcp" or "raw" && n.Transport is "tcp" or "raw"))).ToArray();
        if (peers.Length > 0)
        {
            var comparison = new Expander { Header = "比较其他 VPS 的同类节点", Margin = new Thickness(0, 4, 0, 6) };
            comparison.Expanded += (_, _) =>
            {
                var speeds = _store.LatestDownloads();
                var hosts = _store.Hosts.ToDictionary(h => h.Id);
                comparison.Content = Ui.Table(peers.Prepend(n).Select(peer => new {
                    peer.Id, peer.Name, Host = HostName(hosts.GetValueOrDefault(peer.HostId)),
                    Speed = speeds.TryGetValue(peer.Id, out var rate) ? $"{Number(rate, "Mbps")} · {Time(rate)}" : "未测速",
                    Delay = Number(latest.GetValueOrDefault(("node", peer.Id)), "ms"),
                    DelayValue = latest.GetValueOrDefault(("node", peer.Id))?.Value
                }), [("VPS", "Host", -1), ("节点", "Name", -1.3), ("最近下载", "Speed", -1.4), ("最近延迟", "Delay", 90)],
                    ("详情", item => OpenNode(item.Id)));
            };
            root.Children.Add(comparison);
        }
        var trend = Ui.Stack(Ui.Row(Ui.Section("延迟趋势"), Ui.Button("24 小时", () => { _historyDays = 1; Render(); }, _historyDays == 1),
                Ui.Button("7 天", () => { _historyDays = 7; Render(); }, _historyDays == 7)),
            new TrendChart(history, end.AddDays(-_historyDays), end, TimeSpan.FromMinutes(_store.Settings.IntervalMinutes), false),
            StateStrip(history));
        root.Children.Add(Ui.Columns(Ui.Card(trend), Ui.Card(Ui.Stack(Ui.Section("下载速度 · 手动测速"),
            new TrendChart(downloads, end.AddDays(-_historyDays), end, TimeSpan.Zero, true)))));
        if (n.Security == "reality") root.Children.Add(Reality(n, latest));
        return root;
    }

    private static FrameworkElement StateStrip(Observation[] history)
    {
        var blocks = Ui.Row(Ui.Text("巡检状态  ", brush: "Quiet"));
        foreach (var sample in history.TakeLast(28))
        {
            var state = sample.State == "可用" ? "Good" :
                sample.State is "依赖缺失" or "参数待验证" or "待补全" ? "Quiet" : "Bad";
            var square = new Border
            {
                Width = 11, Height = 13, Margin = new Thickness(2), CornerRadius = new CornerRadius(2),
                ToolTip = $"{Time(sample)} · {sample.State}"
            };
            Ui.Brush(square, Border.BackgroundProperty, state);
            blocks.Children.Add(square);
        }
        if (history.Length == 0) blocks.Children.Add(Ui.Text("尚无结果", brush: "Quiet"));
        return blocks;
    }

    private FrameworkElement ConfigTabs(Node n)
    {
        var tabs = new TabControl();
        ShareLink? link = null;
        string missing = "";
        try { link = ShareLink.Parse(ProxyProbe.ClientLink(_store, n)); }
        catch (InvalidDataException ex) { missing = ex.Message; }
        var host = _store.Hosts.FirstOrDefault(x => x.Id == n.HostId);
        void Add(string name, UIElement content) => tabs.Items.Add(new TabItem { Header = name, Content = content });
        Add("客户端", link is null ? Ui.Text(missing) : Fields(link.Fields().ToArray()));
        Add("服务端", n.Server.Count == 0 ? Ui.Text("待补服务端配置，可从 VPS 核对或手工录入。") :
            Fields(n.Server.Select(f => new DisplayField(f.Key, f.Value, ShareLink.IsSensitive(f.Key))).ToArray()));
        Add("回落与关联", Fields(n.Related.Select(f => new DisplayField(f.Key, f.Value, ShareLink.IsSensitive(f.Key)))
            .Concat(n.Server.Where(x => x.Key == "回落").Select(f => new DisplayField(f.Key, f.Value, false)))
            .DefaultIfEmpty(new("状态", "尚未登记回落、域名或外部依赖。", false)).ToArray()));
        Add("复建清单", Ui.Stack(Fields([
            new("来源", n.Source, false),
            new("关联 VPS", HostName(host), false),
            new("最近核对", n.InventoriedAt?.ToLocalTime().ToString("g") ?? "", false),
            new("1. 核对部署依赖", "确认版本、端口、DNS、回落及证书。保留所需密钥的原保管位置。", false),
            new("2. 服务端入站", n.Server.Count > 0 ? "按服务端和回落页录入。核对云侧和主机侧放行。" : "待补服务端配置。", false),
            new("3. 客户端", link is null ? "待补客户端配置。" : "按客户端页录入，或通过分享导入。", false),
            new("4. 验证", "在空白测试机复建后，执行真实代理请求。", false)]),
            Ui.Button("编辑服务端及关联资料", () => EditNode(n))));
        if (n.Sources.Length > 0)
            Add("来源记录", Ui.Stack(Ui.Text("来源原始配置保存在加密台账与备份中；下方仅显示来源名称和状态。", brush: "Quiet"),
                Ui.Table(n.Sources.Select(s => new { s.Kind, s.Name, s.Directory, State = s.Removed ? "来源已移除" : "保留" }),
                    [("来源", "Kind", -1), ("原名称", "Name", -1.5), ("本机目录", "Directory", -2), ("状态", "State", 100)])));
        tabs.SelectedIndex = Math.Clamp(_configurationTab, 0, tabs.Items.Count - 1);
        tabs.SelectionChanged += (_, _) => _configurationTab = tabs.SelectedIndex;
        return tabs;
    }

    private Border Reality(Node node, System.Collections.Generic.Dictionary<(string, string), Observation> last)
    {
        Border Card(string label, string kind)
        {
            var value = last.GetValueOrDefault((kind, node.Id));
            return Ui.Card(Ui.Stack(Ui.Text(label, bold: true), new StatusPill { Value = State(value) }, Ui.Text(Time(value), brush: "Quiet"),
                Ui.Value(value?.Detail ?? "", 13)));
        }
        return Ui.Card(Ui.Stack(Ui.Row(Ui.Section("REALITY 诊断"),
            Ui.Button("一键检测 SNI", () => Run("sni", node.Id), true)),
            Ui.Columns(Card("本机 → SNI 站点", "sni-local"), Card("VPS → 握手目标", "sni-vps"),
                Card("本机 → 代理节点", "sni-node"))));
    }

    private StackPanel WebPage()
    {
        var latest = _store.Latest();
        var hosts = _store.Hosts.ToDictionary(x => x.Id);
        var nodes = _store.Nodes.ToDictionary(x => x.Id);
        var targets = WebProbe.Targets(_store).OrderBy(x => WebProbe.Domain(x)).ToArray();
        var rows = targets.Select(t =>
        {
            var host = hosts.GetValueOrDefault(t.HostId);
            var h80 = latest.GetValueOrDefault(("web80", t.Id));
            var h443 = latest.GetValueOrDefault(("web443", t.Id));
            var tls = latest.GetValueOrDefault(("tls", t.Id));
            var name = Field(h443?.Title ?? "", Field(h80?.Title ?? "", t.Name == WebProbe.Domain(t) ? "" : t.Name));
            var fallback = string.Join("、", t.FallbackNodeIds.Select(id =>
                (nodes.GetValueOrDefault(id)?.Name ?? "待核对") + (t.AutoFallbackNodeIds.Contains(id) ? "（配置）" : "（手工）")));
            var associated = nodes.Values.Where(n => WebProbe.UsesEndpoint(n, t) || t.CertificateNodeIds.Contains(n.Id)).ToArray();
            var unconfirmed = associated.Any(n => n.HostId == t.HostId && WebProbe.HasFallbackRules(n))
                ? "有入站规则，站点待核" : "站点关联未确认";
            var error = new[] { (Port: "80", Value: h80), (Port: "443", Value: h443) }.Where(x =>
                x.Value != null && x.Value.State is not ("有网页" or "跳转后有网页"))
                .Select(x => $"{x.Port} {x.Value!.Detail}");
            var expiry = tls?.Expires is DateTimeOffset expires ? $"{(int)Math.Ceiling((expires - DateTimeOffset.Now).TotalDays)} 天 · {expires:yyyy-MM-dd}" : "—";
            string WebState(int port, Observation? hit)
            {
                var state = t.CheckWebsite ? State(hit) : "○ 已关闭";
                return state.Insert(state.IndexOf(' ') + 1, $"{port} · ");
            }
            return new WebRow(t, $"{WebProbe.Domain(t)}:{t.Port}", HostName(host), host is null ? "—" : Field(string.Join(" / ", new[] { host.Address, host.Ipv6 }.Where(x => x.Length > 0)), "—"),
                string.Join("、", associated.Select(n => n.Name).Distinct().DefaultIfEmpty("无关联节点")), Field(name, "—"), WebState(80, h80),
                WebState(443, h443), t.Location, State(tls), expiry, "回落：" + Field(fallback, unconfirmed) + (_webCertificates ? "" : " " + string.Join(" / ", error)));
        }).ToArray();
        var table = Ui.Table(rows, [], ("详情", item => WebsiteResults(item.Target)), ("编辑", item => EditWebsite(item.Target)));
        table.RowHeight = double.NaN;
        table.MinRowHeight = 49;
        table.MaxHeight = double.PositiveInfinity;
        table.Columns.Insert(0, Ui.PairedColumn(_webCertificates ? "域名 / 检测位置" : "域名 / 网站",
            "DomainPort", _webCertificates ? "Where" : "Website", 1.4));
        table.Columns.Insert(1, Ui.PairedColumn("VPS / 地址", "Vps", "Ip", 1.13));
        table.Columns.Insert(2, _webCertificates ? Ui.PairedColumn("证书 / 到期", "Cert", "Expires", 1.17, true) :
            Ui.PairedColumn("HTTP / HTTPS", "On80", "On443", 1.2, true, true));
        table.Columns.Insert(3, Ui.PairedColumn("关联节点 / Xray 回落",
            "Fallback", "Problem", 1.03));
        return Ui.Stack(Header("网站与证书", Ui.Button("添加网站或证书", () => EditWebsite(null), true),
            Ui.Button("巡检网站与证书", () => Run("websites"))),
            Ui.Row(Ui.Button("网站", () => { _webCertificates = false; Render(); }, !_webCertificates),
                Ui.Button("证书", () => { _webCertificates = true; Render(); }, _webCertificates)),
            new Border { Child = Ui.Card(table), Margin = new Thickness(0, 8, 0, 0) },
            Ui.Text("TLS 节点的证书域名会自动列出。回落关系以盘点或手工登记为准。", brush: "Quiet"));
    }

    private void WebsiteResults(WebTarget target)
    {
        var last = _store.Latest();
        var fields = new[] { ("web80", "HTTP 80"), ("web443", "HTTPS 443"), ("dns", "DNS"),
            ("web", "自定义 URL"), ("tls", "证书") }.Select(x =>
        {
            var hit = last.GetValueOrDefault((x.Item1, target.Id));
            return new DisplayField(x.Item2, $"{State(hit)} · {Time(hit)}  {hit?.Title}  {hit?.Detail}", false);
        }).ToArray();
        var block = Ui.Stack(Fields(fields));
        var dialog = Dialog(target.Name, block, 700, 450);
        var owner = _store.Hosts.FirstOrDefault(h => h.Id == target.HostId);
        if (owner != null) block.Children.Add(Ui.Button($"主机 · {owner.Name}", () => { dialog.Close(); OpenHost(owner.Id); }));
        foreach (var n in _store.Nodes.Where(n => WebProbe.IsRelated(n, target)))
        {
            var roles = new List<string>();
            if (WebProbe.UsesEndpoint(n, target)) roles.Add("连接端点");
            if (target.CertificateNodeIds.Contains(n.Id)) roles.Add("证书使用");
            if (target.FallbackNodeIds.Contains(n.Id)) roles.Add(target.AutoFallbackNodeIds.Contains(n.Id) ? "配置回落（自动）" : "手工登记回落");
            else if (WebProbe.HasFallbackRules(n)) roles.Add("入站有回落规则，站点归属待核");
            block.Children.Add(Ui.Button($"{string.Join(" / ", roles)} · {n.Name}" + (n.HostId.Length == 0 ? "（未归属）" : ""), () => { dialog.Close(); OpenNode(n.Id); }));
        }
        block.Children.Add(Ui.Row(Ui.Button("立即检测", () => { dialog.Close(); Run("web", target.Id); }, true),
            Ui.Button("编辑", () => { dialog.Close(); EditWebsite(target); }),
            Ui.Button("删除", () => { dialog.Close(); RemoveAsset("web", target.Id, target.Name); }),
            Ui.Button("在浏览器打开", () =>
            {
                var domain = WebProbe.Domain(target);
                if (!VPSnode.Core.Validation.Address(domain)) return;
                try
                {
                    var uri = new UriBuilder("https", domain).Uri;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
                }
                catch (Exception ex) { Fail(ex); }
            })));
        dialog.ShowDialog();
    }

    private static StackPanel Fields(DisplayField[] fields)
    {
        var result = Ui.Stack();
        foreach (var f in fields)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var name = Ui.Text(f.Name, brush: "Quiet", bold: true);
            var value = Ui.Value(f.Sensitive && f.Value.Length > 0 ? "••••••••" : Field(f.Value, "未记录"));
            var line = Ui.Stack(value);
            if ((f.Sensitive || f.Value.Length > 110) && f.Value.Length > 0)
            {
                line.Children.Clear();
                line.Children.Add(new Expander { Header = f.Sensitive ? "显示原值" : "展开全文", Content = Ui.Value(f.Value) });
            }
            Grid.SetColumn(line, 1);
            row.Children.Add(name);
            row.Children.Add(line);
            result.Children.Add(row);
        }
        return result;
    }

    private static void Copy(string text)
    {
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { MessageBox.Show("剪贴板暂不可用。"); }
    }
}
