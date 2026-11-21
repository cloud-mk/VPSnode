using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VPSnode.Core;
using VPSnode.Desktop;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0 || Directory.Exists(args[0])) throw new ArgumentException("Supply a fresh isolated directory.");
        var root = Path.GetFullPath(args[0]);
        Environment.SetEnvironmentVariable("VPSNODE_DATA", root);
        var store = new Store(root);
        Require(!store.Settings.ScheduledInspectionEnabled, "fresh install disables scheduled inspection");
        var old = Wire.Decode<Settings>("""{"IntervalMinutes":73,"SpeedBytes":1234567,"FollowGroups":["fixture"]}""");
        Require(!old.ScheduledInspectionEnabled && old.IntervalMinutes == 73 && old.SpeedBytes == 1234567, "legacy settings default off and retain values");
        store.Save(old with { FollowGroups = [] });
        store.Save(new Node { Id = "fixture", Name = "示例长节点名称 / 可选择复制与导航", Paused = true, Protocol = "VLESS", Transport = "xhttp", Port = 443 });
        var monitor = new VPSnode.Core.Monitor(store);
        monitor.Tick(default).GetAwaiter().GetResult();
        Require(store.History("task", "history", DateTimeOffset.MinValue).Length == 0, "disabled scheduler does not start periodic work");
        store.Queue("node", "fixture");
        monitor.Tick(default).GetAwaiter().GetResult();
        Require(store.Peek() == null && store.History("task", "history", DateTimeOffset.MinValue).Length == 1, "CLI queue drains with scheduler disabled");
        monitor.Run("node", "fixture", default).GetAwaiter().GetResult();
        Require(store.History("task", "history", DateTimeOffset.MinValue).Length == 2, "manual task runs with scheduler disabled");
        store.Save(store.Settings with { ScheduledInspectionEnabled = true });
        Require(new Store(root).Settings.ScheduledInspectionEnabled, "enabled choice persists across Store restart");
        monitor.Tick(default).GetAwaiter().GetResult();
        var count = store.History("task", "history", DateTimeOffset.MinValue).Length;
        Require(count == 3, "enabled scheduler starts periodic work");
        monitor.Tick(default).GetAwaiter().GetResult();
        Require(store.History("task", "history", DateTimeOffset.MinValue).Length == count, "interval throttles periodic work");
        store.Save(store.Settings with { ScheduledInspectionEnabled = false });
        typeof(VPSnode.Core.Monitor).GetField("_lastTick", Private)!.SetValue(monitor, DateTimeOffset.MinValue);
        monitor.Tick(default).GetAwaiter().GetResult();
        Require(store.History("task", "history", DateTimeOffset.MinValue).Length == count && !new Store(root).Settings.ScheduledInspectionEnabled, "disable persists and suppresses due work");

        var app = new App(); app.InitializeComponent();
        var window = new MainWindow(); app.MainWindow = window;
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", Private)!.GetValue(window)!).Stop();
        window.Show();
        void Page(string name) { typeof(MainWindow).GetMethod("Go", Private)!.Invoke(window, [name]); window.UpdateLayout(); }
        void Click(string name) => Walk(window).OfType<Button>().First(b => Equals(b.Content, name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        TextBox Input(string name) => Walk(window).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == name);
        Page("settings");
        Require(Input("单次下载流量上限 MB").Text == "1.234567", "byte limit is presented without precision loss");
        var interval = Input("巡检间隔");
        interval.Text = "37";
        var dark = Walk(window).OfType<CheckBox>().Single(b => Equals(b.Content, "深色模式"));
        dark.IsChecked = true;
        Require(interval.Text == "37" && store.Settings.IntervalMinutes == 73, "theme change preserves unsaved draft");
        Click("保存设置");
        Require(store.Settings.IntervalMinutes == 37 && store.Settings.SpeedBytes == 1234567, "atomic save retains byte precision");
        Require(store.Settings.Theme == "dark" && !store.Settings.ScheduledInspectionEnabled, "save retains theme and off state");
        var enabled = Walk(window).OfType<CheckBox>().Single(b => Equals(b.Content, "启用定时巡检"));
        enabled.IsChecked = true; Click("保存设置");
        Require(store.Settings.ScheduledInspectionEnabled && interval.IsEnabled, "settings switch enables and persists");
        enabled.IsChecked = false; Click("保存设置");
        Require(!store.Settings.ScheduledInspectionEnabled && !interval.IsEnabled, "settings switch disables and persists");

        foreach (var isDark in new[] { false, true })
        {
            dark.IsChecked = isDark;
            var password = new PasswordBox { Password = "fictional-test" };
            var dialog = new Window { Owner = window, Content = password, Width = 280, Height = 150 };
            dialog.Show(); dialog.UpdateLayout();
            Require(Equals(password.Foreground, app.FindResource("Text")) && Equals(password.Background, app.FindResource("Surface")), "password theme foreground/background " + isDark);
            dialog.Close();
        }
        Page("nodes");
        var cells = Walk(window).OfType<LedgerTextBox>().ToArray();
        Require(cells.All(c => !c.IsInactiveSelectionHighlightEnabled), "inactive selections are hidden");
        var node = cells.Single(c => c.Text.StartsWith("示例长节点"));
        node.Focus(); node.Select(0, 2);
        Require(node.SelectedText == "示例", "substring selection retained");
        node.DataContext = new object();
        Require(node.SelectionLength == 0, "recycled cell clears stale selection");
        Require(Walk(window).OfType<Button>().Any(b => Equals(b.Content, "测延迟")) && Walk(window).OfType<Button>().Any(b => Equals(b.Content, "测下载")) &&
            !Walk(window).OfType<Button>().Any(b => Equals(b.Content, "分享")), "list has latency and download actions instead of share");
        var formatter = typeof(MainWindow).Assembly.GetType("VPSnode.Desktop.DisplayFormat")!;
        var latency = formatter.GetMethod("Latency", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require((string)latency.Invoke(null, [108.5])! == "109ms", "midpoint rounds away from zero");
        var badgeType = typeof(MainWindow).Assembly.GetType("VPSnode.Desktop.LatencyBadge")!;
        var badge = (Border)Activator.CreateInstance(badgeType)!;
        badgeType.GetProperty("Milliseconds")!.SetValue(badge, 80.4d);
        badgeType.GetProperty("Value")!.SetValue(badge, "80ms");
        Require(((SolidColorBrush)badge.Background).Color == (Color)ColorConverter.ConvertFromString("#FFBD69"), "latency grade uses raw precision rather than rounded display");
        var detail = formatter.GetMethod("Details", BindingFlags.NonPublic | BindingFlags.Static)!;
        Require((string)detail.Invoke(null, ["首次 108.5 ms · 后续 99.2 ms"])! == "首次 109ms · 后续 99ms", "historical detail formatting is consistent");
        foreach (var width in new[] { 680d, 1024d, 1440d })
        {
            window.Width = width; window.Height = 600; window.UpdateLayout();
            var grid = Walk(window).OfType<DataGrid>().Single();
            Require(grid.Columns.Last().ActualWidth >= 218 && grid.Columns[0].ActualWidth >= 160, "node minimum widths retained at " + width);
        }
        var version = typeof(Store).Assembly.GetName().Version!.ToString(3);
        Require(window.Title.Contains(version) && ((TextBlock)window.FindName("BrandVersion")).Text == "v" + version, "brand and window versions agree");
        typeof(MainWindow).GetMethod("Quit", Private)!.Invoke(window, []);
        Console.WriteLine("PASS UI regression suite (in-process; not OS mouse validation)");
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject item)
    {
        yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(item, i))) yield return child;
    }
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
