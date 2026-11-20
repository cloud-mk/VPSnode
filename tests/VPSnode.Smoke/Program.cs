using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using VPSnode.Core;
using VPSnode.Desktop;

internal static class Program
{
    private const string Fake = "vless://00000000-0000-4000-8000-000000000001@node.example.org:443?encryption=none&security=tls&sni=site.example.org&type=xhttp&mode=packet-up&path=%2Fcheck#Fixture";
    private const string Hy = "hysteria2://placeholder@hy.example.org:443?mport=443%2C8443-8450&obfs=salamander&obfs-password=placeholder&sni=hy.example.org";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) throw new InvalidOperationException("Supply an isolated data directory.");
        var root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidOperationException("Use a fresh data directory.");
        var store = new Store(root);
        Environment.SetEnvironmentVariable("VPSNODE_DATA", root);
        StorageAndLinks(store);
        var planHost = new Host { Id = "ports", Name = "ports", RemotePanelPort = 2053, InventoriedAt = DateTimeOffset.Now,
            Ports = [new("UDP","127.0.0.53",53,"systemd-resolve"),new("UDP","*",68,""),new("TCP","*",443,"xray"),
                new("TCP","*",111,"rpcbind"),new("TCP","*",38652,""),new("TCP","::",443,"xray"),new("TCP","127.0.0.1",8080,"python"),new("TCP","*",53,"named")] };
        var plan = new PortPlan(planHost, []);
        Require(plan.Used.Count(p => p.Port == 443) == 1 && plan.Used.Any(p => p.Purpose == "named") &&
            !plan.Used.Any(p => p.Protocol == "UDP" && p.Port is 53 or 68), "business listeners grouped and system DNS/DHCP filtered");
        Require(plan.Candidates.Contains("UDP 443") && !plan.Candidates.Contains("TCP 443") &&
            !plan.Candidates.Contains("TCP 8080") && !plan.Candidates.Contains("TCP 2053") &&
            new PortPlan(planHost with { InventoriedAt = null }, []).Candidates.Length == 0, "port candidates respect protocol, loopback, reservations and missing snapshots");
        Require(plan.Unidentified.Single().Port == 38652 && !plan.Used.Any(p => p.Port == 111), "unidentified listeners folded and system RPC filtered");
        var origin = new Node { Id = "origin", Name = "测试入站", Port = 443, HostId = planHost.Id, VpsVerified = true };
        var web = new WebTarget { Id = "web", Name = "测试网站", HostId = planHost.Id, FallbackNodeIds = [origin.Id], Port = 2083 };
        var shared = new PortPlan(planHost, [origin], [web]);
        Require(shared.Used.Single(p => p.Port == 443).Purpose.Contains(web.Name) && shared.Candidates.Contains("TCP 2083"),
            "fallback names label the origin inbound without reserving the remote certificate port");
        var management = new Store(Path.Combine(root, "management"));
        management.Save(planHost);
        management.Save(origin);
        management.Save(web with { Domain = "fixture.example.org" });
        management.Record(new("speed", origin.Id, DateTimeOffset.Now.AddMinutes(-1), "完成", Value: 13));
        management.Record(new("speed", origin.Id, DateTimeOffset.Now, "超时"));
        Require(management.LatestDownloads()[origin.Id].Value == 13, "speed comparison retains last valid sample");
        management.Delete("host", planHost.Id);
        Require(management.Nodes.Single().HostId == "" && management.Websites.Single().HostId == "", "deleting VPS detaches local assets");
        management.Delete("node", origin.Id);
        Require(management.Nodes.Length == 0 && management.Websites.Single().FallbackNodeIds.Length == 0 && management.LatestDownloads().Count == 0,
            "deleting node cleans associations and history");
        management.Delete("web", web.Id);
        Require(management.Websites.Length == 0, "deleting website removes local endpoint");
        var enc = ShareLink.Parse(Fake.Replace("encryption=none", "encryption=mlkem768x25519plus.native.0rtt.fixture"));
        Require(enc.Fields().Any(f => f.Name == "握手模式" && f.Value == "0RTT") && enc.Fields().Any(f => f.Name == "encryption 原值" && f.Sensitive),
            "VLESS encryption summary protects raw configuration");
        BatchAndRelations(Path.Combine(root, "batch"));
        Synchronization(store);
        Website(store).GetAwaiter().GetResult();
        if (!args.Contains("--core-only")) Desktop(store);
        Console.WriteLine("VPSnode smoke checks passed.");
        return 0;
    }

    private static void BatchAndRelations(string folder)
    {
        var store = new Store(folder);
        var host = new Host { Id = "lab", Name = "Lab" };
        store.Save(host);
        var client = ShareLink.New(Fake, host.Id, "client");
        store.Save(client with { Paused = true });
        store.Save(client with { Id = "removed", HostId = "", SourceRemoved = true });
        store.Save(new Node { Id = "partial", Name = "待补", HostId = host.Id });
        store.Save(store.Settings with { V2Directory = folder });
        var runner = new VPSnode.Core.Monitor(store);
        var snapshots = new List<Inspection>();
        runner.Changed += snapshots.Add;
        runner.Run("nodes", "", default).GetAwaiter().GetResult();
        var last = snapshots.Last();
        Require(snapshots.First().Items.All(i => i.State == "排队") && last.Items.Length == 3 &&
            last.Skipped == 3 && last.Remaining == 0 && last.Items.All(i => i.Detail.Length > 0),
            "batch accounts for paused, removed and incomplete targets including unassigned nodes");
        runner.Changed += r => { if (r.Items.Any(i => i.State == "测试中")) runner.CancelActive(); };
        runner.Run("nodes", "", default).GetAwaiter().GetResult();
        Require(snapshots.Last().State == "已取消" && snapshots.Last().Remaining == 0 &&
            new Store(folder).History("task", "history", DateTimeOffset.MinValue).Length == 2,
            "cancelled batch accounts for unfinished items and survives reopening");
        var endpoint = new WebTarget { Id = "endpoint", Name = "endpoint", Domain = client.Address,
            Sni = ShareLink.Parse(client.Link).Sni, HostId = host.Id, Port = client.Port };
        Require(WebProbe.UsesEndpoint(client, endpoint) && !WebProbe.UsesEndpoint(client, endpoint with { Port = 8443 }) &&
            !WebProbe.UsesEndpoint(client, endpoint with { ConnectAddress = "192.0.2.10" }) &&
            !WebProbe.UsesEndpoint(client, endpoint with { HostId = "other" }) && endpoint.FallbackNodeIds.Length == 0,
            "address, TLS name, port and host constrain endpoint links without inferring fallback");
        Require(WebProbe.HasFallbackRules(client with { VpsVerified = true, Server = new() { ["回落"] = "[{}]" } }) &&
            !WebProbe.HasFallbackRules(client with { VpsVerified = true, Server = new() { ["回落"] = "[]" } }),
            "server fallback evidence is distinct from a confirmed website assignment");
    }

    private static void Require(bool result, string label)
    {
        if (!result) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }

    private static void StorageAndLinks(Store store)
    {
        var config = Path.Combine(store.DirectoryPath, "ssh-config");
        File.WriteAllText(config, " Host first second\nHOST=First # duplicate\nHost * !excluded\n");
        Require(SshInventory.KnownAliases(config).SequenceEqual(["first", "second"]), "SSH picker lists concrete aliases once");
        var host = new Host { Id = "fixture", Name = "Fixture VPS", Address = "192.0.2.24",
            Hostname = "fixture-vps", Enabled = false, Notes = "keep notes" };
        var nodes = new[] { ShareLink.New(Fake, host.Id, "fixture-node") with { Paused = true } }
            .Concat(Enumerable.Range(2, 4).Select(i => ShareLink.New(Fake.Replace("Fixture", $"Fixture{i}")
                .Replace("000000000001@", $"00000000000{i}@"), host.Id, $"fixture-node{i}")
                with { Paused = true })).ToArray();
        var site = new WebTarget { Id = "fixture-web", Name = "Fixture", Domain = "site.example.org", HostId = host.Id,
            FallbackNodeIds = [nodes[0].Id], Enabled = false };
        var doc = new InventoryDocument(1, [host], nodes, [site]);
        Require(store.Import(doc, false).Added == 7 && store.Hosts.Length == 0, "import preview rolls back");
        Require(store.Import(doc, true, false).Added == 7 && store.Import(doc, true, false).Added == 0,
            "transactional import and replay");
        Require(store.Nodes.Count(n => n.HostId == host.Id) == 5, "five nodes under a single host");
        Require(store.Websites.Single().FallbackNodeIds.Single() == nodes[0].Id, "website fallback node persisted");
        var certNode = WebProbe.Targets(store).Single();
        Require(certNode.CertificateNodeIds.Length == 5 && certNode.FallbackNodeIds.Length == 1,
            "certificate users and verified fallback association stay distinct");
        var isolated = new Store(Path.Combine(store.DirectoryPath, "target-tests"));
        isolated.Save(nodes[0]);
        isolated.Save(nodes[1] with { Paused = false });
        var shared = WebProbe.Targets(isolated).Single();
        Require(shared.Enabled && shared.CertificateNodeIds.Length == 2, "active TLS sibling enables shared endpoint");
        isolated.Save(site with { ConnectAddress = "192.0.2.80" });
        Require(WebProbe.Targets(isolated).Length == 2, "origin and public TLS endpoints remain independent");
        try { VPSnode.Core.Validation.Host(host with { Ipv6 = "192.0.2.24" }); throw new Exception("IPv4 accepted as IPv6"); }
        catch (InvalidDataException) { Console.WriteLine("PASS IPv6 field rejects IPv4"); }
        var share = ShareLink.Parse(Fake);
        Require(ShareLink.Parse(share.Format()).Options["mode"] == "packet-up", "share roundtrip retains XHTTP");
        var ray = ProxyProbe.Build(share, "192.0.2.50", 15989);
        var rayConfig = ray.ToJsonString();
        Require(rayConfig.Contains("packet-up") && rayConfig.Contains("site.example.org") && rayConfig.Contains("127.0.0.1"),
            "Xray options preserve SNI when connecting by IP");
        var hy = ProxyProbe.Build(ShareLink.Parse(Hy), "192.0.2.50", 15989).ToJsonString();
        Require(hy.Contains("server_ports") && hy.Contains("salamander"), "Hysteria2 port hopping and obfuscation");
        var hopping = ProxyProbe.Build(ShareLink.Parse(Hy + "&hopInterval=10-30"), "192.0.2.50", 15989)["outbounds"]![0]!;
        Require((string?)hopping["hop_interval"] == "10s" && (string?)hopping["hop_interval_max"] == "30s",
            "v2rayN hopping interval range converted to sing-box durations");
        try { ShareLink.Parse(Fake.Replace(":443?", ":0?")); throw new Exception("zero port accepted"); }
        catch (InvalidDataException) { Console.WriteLine("PASS explicit port zero rejected"); }
        var misunderstood = ShareLink.Parse(Fake.Replace("#", "&unknown=future#"));
        Require(ProxyProbe.Unsupported(misunderstood) != null, "unsupported parameters produce an explicit result");
        store.Record(new("node", nodes[0].Id, DateTimeOffset.Now.AddMinutes(-2), "可用", Value: 73));
        store.Record(new("node", nodes[0].Id, DateTimeOffset.Now, "超时"));
        Require(store.Latest()[("node", nodes[0].Id)].State == "超时", "latest finding stays separate from historical success");
    }

    private static void Synchronization(Store store)
    {
        var folder = Path.Combine(store.DirectoryPath, "v2-fixture");
        Directory.CreateDirectory(Path.Combine(folder, "guiConfigs"));
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = V2Sync.Database(folder) }.ToString());
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            CREATE TABLE SubItem (Id TEXT, Remarks TEXT);
            INSERT INTO SubItem VALUES ('group-a','Local A'), ('group-b','Local B');
            CREATE TABLE ProfileItem (IndexId TEXT, Subid TEXT, Address TEXT, ConfigType INT, Port INT, Password TEXT,
                Remarks TEXT, Network TEXT, StreamSecurity TEXT, Sni TEXT, TransportExtra TEXT, ProtoExtra TEXT);
            INSERT INTO ProfileItem VALUES ('p1','group-a','node.example.org',5,443,'00000000-0000-4000-8000-000000000002',
                'First', 'xhttp','tls','site.example.org','{"XhttpMode":"packet-up","Path":"/first"}','{"VlessEncryption":"none"}'),
            ('p2','group-a','node.example.org',5,443,'00000000-0000-4000-8000-000000000003',
                'Second','tcp','tls','site.example.org','',''),
            ('p3','group-b','node.example.org',5,443,'00000000-0000-4000-8000-000000000004',
                'Third','tcp','tls','site.example.org','',''),
            ('p4','group-a','node.example.org',5,443,'00000000-0000-4000-8000-000000000005',
                'Moved','tcp','tls','site.example.org','','');
            """;
        command.ExecuteNonQuery();
        Require(V2Sync.Apply(store, folder, ["group-a","group-b"], false).Added == 4, "read-only v2rayN local groups");
        var probeStore = new Store(Path.Combine(store.DirectoryPath, "probe-match"));
        probeStore.Save(new Host { Id = "probe", Name = "Probe", Address = "node.example.org" });
        probeStore.Save(probeStore.Settings with { V2Directory = folder });
        var inbound = new Node { Id = "test-inbound", Name = "Inventory", HostId = "probe", VpsVerified = true,
            Protocol = "VLESS", Transport = "raw", Security = "tls", Port = 443,
            Server = new() { ["原始入站设置"] = """{"clients":[{"id":"00000000-0000-4000-8000-000000000003"}]}""" } };
        string ProbeState() => ProxyProbe.Run(probeStore, inbound, false, default).GetAwaiter().GetResult().State;
        Require(ProbeState() == "依赖缺失", "unique authenticated client matched before checking for the test core");
        inbound = inbound with { Server = new() { ["原始入站设置"] =
            """{"clients":[{"id":"00000000-0000-4000-8000-000000000003"},{"id":"00000000-0000-4000-8000-000000000004"}]}""" } };
        Require(ProbeState() == "待补全", "ambiguous client profiles are not silently selected");
        var vpsVerified = store.Nodes.Single(x => x.V2Id == "p2");
        store.Save(vpsVerified with { VpsVerified = true });
        var first = store.Nodes.Single(x => x.V2Id == "p1");
        store.Record(new("node", first.Id, DateTimeOffset.Now, "可用", Value: 67));
        command.CommandText = "DELETE FROM ProfileItem WHERE IndexId <> 'p4'; UPDATE ProfileItem SET Subid='group-b' WHERE IndexId='p4'";
        command.ExecuteNonQuery();
        var sync = V2Sync.Apply(store, folder, ["group-a"], true);
        var final = store.Nodes.Where(x => x.V2Id.Length > 0).ToDictionary(x => x.V2Id);
        Require(sync.Paused == 1 && final["p1"].SourceRemoved && !final["p2"].SourceRemoved &&
            !final["p3"].SourceRemoved && !final["p4"].SourceRemoved, "pause only removed v2rayN-only item in followed group");
        Require(store.History("node", first.Id, DateTimeOffset.MinValue).Length == 1, "auto pause preserves history");
        foreach (var item in final.Values) store.Save(item with { Paused = true });
    }

    private static async Task Website(Store store)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(limit.Token);
            using var stream = connection.GetStream();
            var request = new byte[2048];
            if (await stream.ReadAsync(request, limit.Token) == 0) throw new EndOfStreamException();
            var document = Encoding.UTF8.GetBytes("<!doctype html><html><head><title>示例 &amp; 电台</title></head><body>播放</body></html>");
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {document.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, limit.Token);
            await stream.WriteAsync(document, limit.Token);
        }, limit.Token);
        var url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var target = new WebTarget { Id = "local-http", Name = "Local HTTP", Url = url, CheckWebsite = false };
        var web = (await WebProbe.Run(target, false, limit.Token)).Single(x => x.Kind == "web");
        await server;
        Require(web.State == "正常" && web.Title == "示例 & 电台" && web.HttpStatus == 200, "HTTP reports the real HTML title and status");
    }

    private static void Desktop(Store store)
    {
        var app = new App();
        app.InitializeComponent();
        store.Save(store.Settings with { Theme = "dark" });
        var window = new MainWindow();
        app.MainWindow = window;
        window.Show();
        const BindingFlags nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(MainWindow);
        ((DispatcherTimer)type.GetField("_timer", nonPublic)!.GetValue(window)!).Stop();
        Require(((SolidColorBrush)app.Resources["Canvas"]).Color == Color.FromRgb(8, 13, 21), "stored dark theme applied at startup");
        var go = type.GetMethod("Go", nonPublic)!;
        go.Invoke(window, ["settings"]);
        window.UpdateLayout();
        var theme = Walk(window).OfType<CheckBox>().Single(c => Equals(c.Content, "深色模式"));
        theme.IsChecked = false;
        Require(store.Settings.Theme == "light" && ((SolidColorBrush)app.Resources["Canvas"]).Color == Color.FromRgb(239, 243, 249),
            "changing the theme checkbox updates both stored and visible theme");
        foreach (var route in new[] { "hosts", "nodes", "web", "settings", "overview" })
            go.Invoke(window, [route]);
        store.Record(new("speed", "fixture-node", DateTimeOffset.Now.AddMinutes(-2), "完成", Value: 17));
        go.Invoke(window, ["nodes"]);
        window.UpdateLayout();
        Require(Walk(window).OfType<ProgressBar>().Any(bar => bar.IsVisible && bar.Value == 17) &&
            Walk(window).OfType<ProgressBar>().Any(bar => bar.Visibility == Visibility.Collapsed),
            "measured nodes show a proportional speed strip and unmeasured samples are hidden");
        var cell = Walk(window).OfType<TextBox>().First(t => t.IsReadOnly && t.Text == "Fixture");
        cell.Select(0, 3);
        Require(cell.SelectedText == "Fix" && System.Windows.Input.ApplicationCommands.Copy.CanExecute(null, cell),
            "node cell supports selecting a substring and the Windows copy command");
        type.GetMethod("OpenHost", nonPublic)!.Invoke(window, ["fixture"]);
        window.UpdateLayout();
        type.GetMethod("Back", nonPublic)!.Invoke(window, []);
        Require((string?)type.GetField("_page", nonPublic)!.GetValue(window) == "nodes", "Back restores the originating page");
        void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        go.Invoke(window, ["hosts"]);
        window.UpdateLayout();
        Click(Walk(window).OfType<Button>().Single(b => Equals(b.ToolTip, "查看此 VPS 的节点")));
        Require((string?)type.GetField("_nodeHostFilter", nonPublic)!.GetValue(window) == "fixture",
            "clicking host node count opens that host filter");
        window.UpdateLayout();
        Click(Walk(window).OfType<Button>().Single(b => Equals(b.Content, "全测延迟")));
        var until = DateTime.UtcNow.AddSeconds(12);
        while (type.GetField("_current", nonPublic)!.GetValue(window) != null && DateTime.UtcNow < until)
        {
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
        Click((Button)window.FindName("TasksButton"));
        window.UpdateLayout();
        var completed = Walk(window).OfType<Expander>().First(e => e.Tag is Inspection);
        completed.IsExpanded = true;
        window.UpdateLayout();
        var batch = (Inspection)completed.Tag;
        Require(batch.Kind == "nodes" && batch.Items.Length == store.Nodes.Length && batch.Items.Any(i => i.Id != "fixture-node") &&
            Walk(completed).OfType<DataGrid>().Single().Items.Count == store.Nodes.Length,
            "global delay button covers all groups while filtered and task history opens per-item results");
        go.Invoke(window, ["settings"]);
        window.UpdateLayout();
        Require(Walk(window).OfType<TextBox>().Any(t => t.Text.Contains("构建于") && t.Text.Contains(typeof(Store).Assembly.GetName().Version!.ToString(3))),
            "About shows assembly version and embedded build date");
        window.Width = 1008;
        window.Height = 690;
        go.Invoke(window, ["web"]);
        window.UpdateLayout();
        var scroll = (ScrollViewer)window.FindName("Scroller");
        Require(scroll.ScrollableWidth < 1, "website list fits a 1024-wide work area");
        var domainField = Walk(window).OfType<LedgerTextBox>().First(t => t.Text.StartsWith("site.example.org"));
        Require(domainField.ContextMenu.Items.OfType<MenuItem>().Any(m => Equals(m.Header, "复制")) &&
            domainField.SelectionOpacity == 1 && domainField.SelectionBrush != domainField.SelectionTextBrush,
            "website table values have an explicit Copy menu and contrasting text selection");
        var webGrid = Walk(window).OfType<DataGrid>().Single();
        var lastColumn = Walk(webGrid).OfType<System.Windows.Controls.Primitives.DataGridColumnHeader>().First(c => Equals(c.Content, "操作"));
        Require(lastColumn.TranslatePoint(new Point(lastColumn.ActualWidth, 0), scroll).X <= scroll.ActualWidth,
            "website row actions visible without horizontal scrolling");
        store.Record(new("speed", "fixture-node", DateTimeOffset.Now.AddMinutes(-1), "完成", Value: 42));
        store.Record(new("speed", "fixture-node", DateTimeOffset.Now, "待补全", "请补充分享链接"));
        type.GetMethod("OpenNode", nonPublic)!.Invoke(window, ["fixture-node"]);
        window.UpdateLayout();
        Require(Walk(window).OfType<TextBlock>().Any(b => b.Text.Contains("最近测速") && b.Text.Contains("请补充分享链接")),
            "latest speed failure remains visible after a previous success");
        Require(window.Title == "VPSnode" && window.IsVisible, "WPF window and desktop pages open");
        var qrFound = false;
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            var dialog = window.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.Title == "分享节点");
            if (dialog is null) return;
            qrFound = Walk(dialog).OfType<Image>().Any(image => image.Width >= 400 && image.Source is not null);
            dialog.Close();
        };
        timeout.Start();
        type.GetMethod("Share", nonPublic)!.Invoke(window, [store.Nodes.Single(n => n.Id == "fixture-node")]);
        Require(qrFound, "share dialog renders a locally generated QR at 400 DIP");
        type.GetMethod("Quit", nonPublic)!.Invoke(window, []);
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject item)
    {
        yield return item;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(item, i))) yield return child;
    }
}
