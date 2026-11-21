using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VPSnode.Core;
using VPSnode.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0 || Directory.Exists(args[0])) throw new ArgumentException("Fresh isolated data directory required.");
        if (Environment.GetEnvironmentVariable("VPSNODE_TEST_CULTURE") is string culture)
            System.Globalization.CultureInfo.CurrentCulture = new(culture);
        var root = Path.GetFullPath(args[0]); Environment.SetEnvironmentVariable("VPSNODE_DATA", root);
        var store = new Store();
        store.Save(new Host { Id = "a", Name = "示例 VPS A", Address = "192.0.2.1", Enabled = false });
        store.Save(new Host { Id = "b", Name = "示例 VPS B", Address = "192.0.2.2", Enabled = false });
        const string identity = "00000000-0000-4000-8000-000000000010";
        var fixture = ShareLink.New($"vless://{identity}@192.0.2.1:443?type=tcp&security=tls&sni=fixture.example.org", "a", "n00") with { Name = "示例节点 00" };
        for (int i = 0; i < 36; i++)
            store.Save(fixture with { Id = $"n{i:00}", Name = $"示例节点 {i:00}", HostId = i < 12 ? "a" : i < 24 ? "b" : "", Manual = i != 1,
                Link = i < 3 ? fixture.Link : fixture.Link.Replace(identity, $"00000000-0000-4001-8000-{i:000000000000}"),
                Source = i == 1 ? "v2rayN" : "本机导入", V2Id = i == 1 ? "fictional-source" : "" });
        var app = new App(); app.InitializeComponent();
        if (args.Contains("--ui"))
        {
            app.Startup += (_, _) => app.Dispatcher.BeginInvoke(new Action(() =>
            {
                var main = (MainWindow)app.MainWindow;
                ((DispatcherTimer)typeof(MainWindow).GetField("_timer", Private)!.GetValue(main)!).Stop();
                typeof(MainWindow).GetMethod("Go", Private)!.Invoke(main, ["nodes"]);
            }));
            app.Run(); return;
        }
        var window = new MainWindow(); app.MainWindow = window; window.Show();
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", Private)!.GetValue(window)!).Stop();
        void Call(string name, params object[] values) => typeof(MainWindow).GetMethod(name, Private | BindingFlags.DeclaredOnly)!.Invoke(window, values);
        void Pump() { window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
        DataGrid[] Grids() => Walk(window).OfType<DataGrid>().ToArray();
        void Page(string name) { Call("Go", name); Pump(); }
        try
        {
            Page("nodes"); window.Width = 1280; window.Height = 600; Pump();
            var grids = Grids(); Check(grids.Length == 3, "two VPS groups and unassociated group rendered");
            void Aligned() { foreach (var grid in Grids().Skip(1)) Check(grid.Columns.Select(c => c.ActualWidth).Zip(Grids()[0].Columns.Select(c => c.ActualWidth)).All(p => Math.Abs(p.First - p.Second) < .5), "group columns aligned"); }
            Aligned();
            grids[1].Columns[0].Width = 275; grids[2].Columns[3].Width = 180; Pump(); Aligned();
            var page = ((ContentControl)window.FindName("PageBody")).Content;
            // Group ordering follows the user's culture; locate the fixture by stable ID.
            bool IsTarget(object item) => ((Node)item.GetType().GetProperty("Node")!.GetValue(item)!).Id == "n01";
            var targetGrid = grids.Single(g => g.Items.Cast<object>().Any(IsTarget));
            var row = targetGrid.Items.Cast<object>().Single(IsTarget); targetGrid.SelectedItem = row;
            var selected = Walk(targetGrid).OfType<LedgerTextBox>().First(t => t.Text == "示例节点 01"); selected.Focus(); selected.Select(0, 2);
            var scroll = (ScrollViewer)window.FindName("Scroller"); scroll.ScrollToVerticalOffset(120); Pump();
            var offset = scroll.VerticalOffset; var notices = new List<string>();
            ((INotifyPropertyChanged)row).PropertyChanged += (_, e) => notices.Add(e.PropertyName!);
            var now = DateTimeOffset.Now;
            foreach (var state in new[] { "测试中", "成功", "失败", "超时", "跳过" })
            {
                if (state == "成功") store.Record(new("node", "n01", DateTimeOffset.Now, "可用", Value: 91.8));
                var task = new Inspection("nodes", "fixture", now, state == "测试中" ? "运行中" : "已取消",
                    [new("node", "n01", "示例节点 01", state, state == "跳过" ? "执行中取消" : "")]);
                Task.Run(() => Call("TaskChanged", task)).GetAwaiter().GetResult(); Pump();
                Check(ReferenceEquals(page, ((ContentControl)window.FindName("PageBody")).Content) && Grids().SequenceEqual(grids), "progress preserves page and all grids: " + state);
                Check(ReferenceEquals(row, targetGrid.SelectedItem) && selected.SelectedText == "示例" && Math.Abs(offset - scroll.VerticalOffset) < .5, "row/text selection and scroll preserved: " + state);
                Check(grids.All(g => Math.Abs(g.Columns[0].ActualWidth - 275) < .5 && Math.Abs(g.Columns[3].ActualWidth - 180) < .5), "manual widths preserved: " + state);
                Check((string)row.GetType().GetProperty("TaskState")!.GetValue(row)! == (state == "跳过" ? "跳过 · 执行中取消" : state), "final task state displayed: " + state);
            }
            Check(notices.Contains("Delay") && !notices.Contains("Name") && !notices.Contains("Protocol"), "only changed row properties notified");
            notices.Clear(); Call("RefreshVisibleData"); Pump(); Check(notices.Count == 0, "unchanged observations do not refresh row bindings");
            store.Record(new("speed", "n01", DateTimeOffset.Now, "完成", Value: 25)); Call("RefreshVisibleData"); Pump();
            Check(Math.Abs(offset - scroll.VerticalOffset) < .5 && (double)row.GetType().GetProperty("SpeedMaximum")!.GetValue(row)! == 25, "download result updates scale without changing row height or scroll");
            foreach (var width in new[] { 680d, 1024d, 1440d, 1024d })
            {
                window.Width = width; Pump(); Aligned();
                Check(grids.All(g => Math.Abs(g.Columns[0].ActualWidth - 275) < .5 && Math.Abs(g.Columns[3].ActualWidth - 180) < .5), "manual widths survive window resize " + width);
                if (width == 680) Check(grids.All(g => ((ScrollViewer)g.Template.FindName("DG_ScrollViewer", g)).ScrollableWidth > 100), "narrow view has horizontal scrolling");
            }
            window.Width = 680; Pump();
            ((ScrollViewer)grids[1].Template.FindName("DG_ScrollViewer", grids[1])).ScrollToHorizontalOffset(120); Pump();
            Check(grids.All(g => Math.Abs(((ScrollViewer)g.Template.FindName("DG_ScrollViewer", g)).HorizontalOffset - 120) < .5), "horizontal scroll stays aligned across groups");
            grids[1].Columns[1].DisplayIndex = 0;
            typeof(DataGrid).GetMethod("OnColumnReordered", Private)!.Invoke(grids[1], [new DataGridColumnEventArgs(grids[1].Columns[1])]); Pump();
            Check(grids.All(g => g.Columns[1].DisplayIndex == 0 && g.Columns[0].DisplayIndex == 1), "native column reordering synchronizes across groups");
            window.Width = 1024; Pump();
            Page("hosts"); Page("nodes"); Aligned(); Check(Math.Abs(Grids()[0].Columns[0].ActualWidth - 275) < .5, "navigation retains shared widths");
            grids[0].Columns[0].Width = 350; Pump(); Check(Math.Abs(Grids()[0].Columns[0].ActualWidth - 275) < .5, "retired grid subscriptions are detached");
            typeof(MainWindow).GetField("_search", Private)!.SetValue(window, "示例节点 00"); Call("Render"); Pump();
            Check(Grids().Length == 1 && Math.Abs(Grids()[0].Columns[0].ActualWidth - 275) < .5, "search retains width state");
            typeof(MainWindow).GetField("_search", Private)!.SetValue(window, ""); Call("Render"); Pump();
            store.Save(fixture with { Id = "new-group", Name = "新增分组节点", HostId = "c" });
            store.Save(new Host { Id = "c", Name = "示例 VPS C", Address = "192.0.2.3", Enabled = false });
            Call("RefreshVisibleData"); Pump(); Check(Grids().Length == 4, "new group appears after structural data change"); Aligned();
            store.Delete("node", "new-group"); Call("RefreshVisibleData"); Pump(); Check(Grids().Length == 3, "removed group disappears"); Aligned();
            foreach (var theme in new[] { "light", "dark" })
            {
                typeof(MainWindow).Assembly.GetType("VPSnode.Desktop.Ui")!.GetMethod("Theme", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [theme]); Pump();
                var headers = Walk(Grids()[0]).OfType<DataGridColumnHeader>().Where(h => h.Column != null).ToArray();
                Check(headers.Take(headers.Length - 1).All(h => h.BorderThickness.Right == 1) && headers.Last().BorderThickness.Right == 0, "header-only interior separators " + theme);
                Check(Walk(headers[0]).OfType<Thumb>().Any(t => t.Cursor == System.Windows.Input.Cursors.SizeWE), "native resize grip and cursor retained " + theme);
                foreach (var scale in new[] { 1d, 1.5d })
                {
                    var visual = (FrameworkElement)window.Content;
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * scale), (int)Math.Ceiling(visual.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(visual); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(root, $"nodes-{theme}-{scale * 100}.png")); png.Save(file);
                }
            }
            CheckReview(window, Call, Pump);
            Console.WriteLine("PASS stability suite: actual WPF controls and Dispatcher, not OS mouse or physical DPI validation");
        }
        finally
        {
            foreach (var owned in window.OwnedWindows.OfType<Window>().ToArray()) owned.Close();
            typeof(MainWindow).GetField("_closing", Private)!.SetValue(window, true); window.Close(); app.Shutdown();
        }
    }

    private static void CheckReview(MainWindow window, Action<string, object[]> call, Action pump)
    {
        var page = ((ContentControl)window.FindName("PageBody")).Content;
        Exception? failure = null;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            var review = window.OwnedWindows.OfType<Window>().Single(w => w.Title == "核对重复节点");
            try
            {
                var left = Walk(review).OfType<Button>().Where(b => Equals(b.Content, "查看左侧")).ToArray();
                var right = Walk(review).OfType<Button>().Where(b => Equals(b.Content, "查看右侧")).ToArray();
                void Click(Button button) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); pump(); }
                Click(left[0]); var first = review.OwnedWindows.OfType<Window>().Single();
                Click(right[0]); Check(review.OwnedWindows.Count == 2 && review.IsVisible && ReferenceEquals(page, ((ContentControl)window.FindName("PageBody")).Content), "both read-only views coexist without navigation or closing review");
                Click(left[0]); Click(right[0]); Check(review.OwnedWindows.Count == 2 && review.OwnedWindows.OfType<Window>().Contains(first), "repeated clicks activate existing views");
                Check(review.OwnedWindows.OfType<Window>().SelectMany(Walk).OfType<Button>().All(b => b.Content is not string s || (!s.Contains("编辑") && s != "返回" && s != "趋势")), "comparison views have no editing or navigation actions");
                Check(review.OwnedWindows.OfType<Window>().SelectMany(Walk).OfType<TextBox>().All(t => t.IsReadOnly && !t.IsVisible || t.IsReadOnly && !t.Text.Contains("00000000-0000-4000-8000-000000000010")), "visible comparison credentials remain masked");
                Check(Walk(review.OwnedWindows.OfType<Window>().Single(w => w.Title.StartsWith("右侧"))).OfType<TextBox>().Any(t => t.Text.Contains("v2rayN")), "right source is shown from actual metadata");
                var old = review.OwnedWindows.OfType<Window>().ToArray(); Click(left[1]);
                Check(review.OwnedWindows.Count == 1 && old.All(w => !w.IsVisible), "switching pair closes previous pair views");
                var child = review.OwnedWindows.OfType<Window>().Single(); child.Close(); Click(left[1]);
                Check(review.OwnedWindows.Count == 1 && !ReferenceEquals(child, review.OwnedWindows[0]), "closed side can be reopened once");
                var remaining = review.OwnedWindows.OfType<Window>().Single(); review.Close(); pump();
                Check(!remaining.IsVisible && window.OwnedWindows.Count == 0, "closing review disposes all configuration windows");
            }
            catch (Exception ex) { failure = ex; review.Close(); }
        }));
        call("ShowDuplicateNodes", []); if (failure != null) throw failure;
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Walk(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    private static void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
}
