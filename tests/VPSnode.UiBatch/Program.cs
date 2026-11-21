using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VPSnode.Core;
using VPSnode.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Use a fresh isolated data directory.");
        var path = Path.GetFullPath(args[0]);
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) throw new InvalidOperationException("Fresh directory required.");
        Environment.SetEnvironmentVariable("VPSNODE_DATA", path);
        var store = new Store(path);
        store.Save(new Settings { Theme = "dark", IntervalMinutes = 1440, V2Directory = Path.Combine(path, "missing-core") });
        store.Save(new Host { Id = "fixture-a", Name = "示例 VPS A", Address = "192.0.2.1", Enabled = false });
        store.Save(new Host { Id = "fixture-b", Name = "示例 VPS B", Address = "192.0.2.2", Enabled = false });
        for (var i = 0; i < 7; i++)
        {
            var id = "fixture-" + i;
            store.Save(new Node { Id = id, Name = new[] { "示例低延迟", "示例中延迟", "示例高延迟", "示例未测速", "示例超时", "示例暂停", "示例来源删除" }[i],
                HostId = i < 3 ? "fixture-a" : i < 5 ? "fixture-b" : "", Protocol = "VLESS", Transport = "tcp", Security = "tls", Port = 443,
                Paused = i == 5, SourceRemoved = i == 6 });
            if (i < 3) store.Record(new("node", id, DateTimeOffset.Now, "可用", Value: new double[] { 80, 160, 161 }[i]));
            if (i == 0 || i == 2) store.Record(new("speed", id, DateTimeOffset.Now.AddDays(-1), "完成", Value: i == 0 ? 10 : 100));
            if (i == 4) store.Record(new("node", id, DateTimeOffset.Now, "超时"));
        }
        if (args.Contains("--screenshots"))
        {
            CapturePages(path, store);
            return;
        }
        if (args.Contains("--ui"))
        {
            var app = new App(); app.InitializeComponent();
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                var window = (MainWindow)app.MainWindow;
                window.Title = "VPSnode UI " + typeof(Store).Assembly.GetName().Version!.ToString(3) + " 验收"; window.Width = 1180; window.Height = 740;
                ((DispatcherTimer)typeof(MainWindow).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
            }));
            app.Run();
            return;
        }
        VerifyUi(path, store);
        CheckBatch(store).GetAwaiter().GetResult();
    }

    private static void CapturePages(string path, Store store)
    {
        store.Save(store.Settings with { V2Directory = "" });
        foreach (var (state, itemState, minutes) in new[] { ("已结束", "成功", 15), ("已结束", "失败", 10), ("已取消", "跳过", 5) })
        {
            var at = DateTimeOffset.Now.AddMinutes(-minutes);
            var inspection = new Inspection("nodes", "手动", at, state,
                [new("node", "fixture-0", "示例低延迟", itemState, itemState == "成功" ? "可用 · 后续 80.4 ms" : itemState == "失败" ? "示例连接失败" : "用户取消")]);
            store.Record(new("task", "history", at, state, Wire.Encode(inspection)));
        }
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(); app.MainWindow = window;
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
        window.Show();
        foreach (var theme in new[] { "light", "dark" })
        foreach (var width in new[] { 1280d, 1024d, 680d })
        foreach (var page in new[] { "nodes", "settings", "tasks" })
        {
            store.Save(store.Settings with { Theme = theme });
            typeof(MainWindow).Assembly.GetType("VPSnode.Desktop.Ui")!.GetMethod("Theme", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [theme]);
            window.Width = width; window.Height = width == 680 ? 460 : 800;
            typeof(MainWindow).GetMethod("Go", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [page]);
            window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            if (page == "nodes" && width == 1280 && theme == "light")
                foreach (var table in Walk(window).OfType<DataGrid>())
                {
                    var scroll = (ScrollViewer)table.Template.FindName("DG_ScrollViewer", table);
                    Console.WriteLine($"Grid width={table.ActualWidth} columns={table.Columns.Sum(c => c.ActualWidth)} viewport={scroll.ViewportWidth} extent={scroll.ExtentWidth} offset={table.CellsPanelHorizontalOffset}");
                    Require(scroll.ScrollableWidth < 1, "wide node table needs no horizontal scrolling");
                }
            foreach (var scale in new[] { 1d, 1.5d, 2d })
            {
                // Captures the running WPF visual tree; this is not an OS-input or physical-monitor DPI test.
                var visual = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                bitmap.Render(visual);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                using var output = File.Create(Path.Combine(path, $"{page}-{theme}-{width}-{scale * 100}.png"));
                png.Save(output);
            }
        }
        typeof(MainWindow).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close(); app.Shutdown();
        Console.WriteLine("Captured running WPF pages with fictional data at 100/150/200% rendering scales.");
    }

    private static void VerifyUi(string path, Store store)
    {
        var app = new App(); app.InitializeComponent();
        var window = new MainWindow();
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!).Stop();
        void Page(string page)
        {
            typeof(MainWindow).GetMethod("Go", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [page]);
            Layout();
        }
        void Layout()
        {
            var view = (FrameworkElement)window.Content;
            view.Measure(new Size(1008, 660)); view.Arrange(new Rect(0, 0, 1008, 660)); view.UpdateLayout();
            foreach (var table in Walk(view).OfType<DataGrid>()) table.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            view.UpdateLayout();
        }
        Page("nodes");
        var content = (FrameworkElement)window.Content;
        var tables = Walk(content).OfType<DataGrid>().ToArray();
        Require(tables.Length == 3 && tables.Sum(t => t.Items.Count) == 7, "node UI groups both VPS and unassigned");
        Require(tables.All(t => Equals(t.RowBackground, t.AlternatingRowBackground)), "no intra-group striping");
        Require(tables.Select(t => ((SolidColorBrush)t.RowBackground).Color).Distinct().Count() == 2, "two group background tones");
        var badges = Walk(content).OfType<Border>().Where(b => b.GetType().Name == "LatencyBadge").ToArray();
        var colors = badges.Select(b => (b.Background as SolidColorBrush)?.Color.ToString()).ToArray();
        Require(colors.Contains("#FF68E8A5") && colors.Contains("#FFFFBD69") && colors.Contains("#FF8E293E"), "80 / 160 / 161 ms badge boundaries");
        Require(Walk(content).OfType<Button>().Any(b => Equals(b.Content, "全测下载速度")), "batch download UI entry exists");
        var bitmap = new RenderTargetBitmap(1008, 660, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var paint = drawing.RenderOpen())
        {
            paint.DrawRectangle((Brush)app.FindResource("Canvas"), null, new Rect(0, 0, 1008, 660));
            paint.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, 1008, 660));
        }
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var output = File.Create(Path.Combine(path, "nodes-offscreen.png"))) png.Save(output);

        // A fresh successful observation must not override a later panel disable.
        // Age alone does not change a successful result or availability count.
        var inboundDisabled = store.Nodes.Single(n => n.Id == "fixture-0");
        var clientDisabled = store.Nodes.Single(n => n.Id == "fixture-1");
        store.Save(inboundDisabled with { Server = new() { ["启用"] = "否" } });
        store.Save(clientDisabled with { Server = new() { ["客户端启用"] = "否" } });
        var stale = new Node { Id = "fixture-stale", Name = "示例过时结果", HostId = "fixture-a" };
        store.Save(stale);
        store.Record(new("node", stale.Id, DateTimeOffset.Now.AddDays(-3), "可用", Value: 55));
        Page("nodes");
        var nodeRows = Walk(content).OfType<DataGrid>().SelectMany(t => t.Items.Cast<object>()).ToArray();
        static T Read<T>(object item, string name) => (T)item.GetType().GetProperty(name)!.GetValue(item)!;
        var disabledRows = nodeRows.Where(r => Read<Node>(r, "Node").Id is "fixture-0" or "fixture-1").ToArray();
        Require(disabledRows.Length == 2 && disabledRows.All(r => Read<string>(r, "Status").Contains("已禁用") && Read<string>(r, "Delay") == "—"),
            "panel or client disable takes priority over previous successful measurement");
        var staleRow = nodeRows.Single(r => Read<Node>(r, "Node").Id == stale.Id);
        Require(Read<string>(staleRow, "Status") == "✓ 可用" && Read<string>(staleRow, "Delay") == "55ms" && Read<string>(staleRow, "Recent") != "—",
            "old successful latency keeps its state and original time");
        Require(Walk(content).OfType<TextBlock>().Any(t => t.Text == "2 可用 · 0 已暂停"), "node group count includes old successes and excludes disabled nodes");
        Page("overview");
        var availableTile = Walk(content).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "节点可用");
        Require(Walk(availableTile).OfType<TextBlock>().Any(t => t.Text == "2 / 8"), "overview includes old successful results");
        Walk(content).OfType<CheckBox>().Single(b => Equals(b.Content, "仅显示异常的 VPS 和节点")).IsChecked = true;
        Layout();
        var anomalyIds = Walk(content).OfType<DataGrid>().SelectMany(t => t.Items.Cast<object>()).Select(r => Read<Node>(r, "Node").Id).ToArray();
        Require(new[] { "fixture-0", "fixture-1" }.All(anomalyIds.Contains) && !anomalyIds.Contains(stale.Id), "anomaly filter retains disabled nodes and excludes old successes");
        store.Save(inboundDisabled); store.Save(clientDisabled); store.Delete("node", stale.Id);

        Page("hosts");
        var counts = Walk(content).OfType<Button>().Where(b => b.ToolTip as string == "查看此 VPS 的节点").ToArray();
        Require(counts.Length == 2 && counts.All(b => b.BorderThickness == new Thickness(0) && b.FontSize == 17), "host count navigation has no border");
        Page("settings");
        Require(Walk(content).OfType<Button>().Count(b => b.Content is "导出加密备份" or "导入恢复") == 2, "settings backup actions present");
        typeof(MainWindow).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true);
        window.Close();
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task CheckBatch(Store store)
    {
        var monitor = new VPSnode.Core.Monitor(store);
        var changes = new List<Inspection>();
        monitor.Changed += changes.Add;
        foreach (var kind in new[] { "nodes", "speeds" })
        {
            await monitor.Run(kind, "", CancellationToken.None);
            var result = changes.Last();
            Require(result.Items.Length == 7 && result.Items.Select(i => i.Id).Distinct().Count() == 7, kind + " includes every node including unassigned");
            Require(result.Items.All(i => i.Kind == (kind == "speeds" ? "speed" : "node")), kind + " reuses individual probe kind");
            Require(result.Skipped == 7 && result.Success == 0 && result.Remaining == 0, kind + " missing config and paused count as skipped");
            Require(result.Items.Single(i => i.Id == "fixture-5").Detail == "已暂停", "paused skip reason");
            Require(changes.Count(c => c.State == "运行中" && c.Remaining < 7) >= 7, "progress publishes completed results");
        }
        monitor.Changed += task => { if (task.State == "运行中") monitor.CancelActive(); };
        await monitor.Run("speeds", "", CancellationToken.None);
        Require(changes.Last().State == "已取消" && changes.Last().Skipped == 7, "cancel marks every unstarted target skipped");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var maintenance = Task.Run(() => monitor.Maintain(() => { held.Set(); release.Wait(TimeSpan.FromSeconds(5)); return true; }, CancellationToken.None));
        Require(held.Wait(TimeSpan.FromSeconds(5)), "maintenance acquires shared gate");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await monitor.Run("nodes", "", cancelled.Token); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { Console.WriteLine("PASS waiting task cancellation"); }
        finally { release.Set(); }
        await maintenance;
        Require(store.History("task", "history", DateTimeOffset.MinValue).Length == 4, "batch and waiting cancellation persist task history");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }
}
