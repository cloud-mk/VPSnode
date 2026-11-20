using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VPSnode.Core;
using VPSnode.Desktop;

// Runs the application's actual WPF event handlers in this process.
// It does not inject OS input, use cross-process UI automation or bypass file pickers.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static MainWindow _window = null!;
    private static Store _store = null!;
    private static readonly List<string> Results = [];

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 0) throw new ArgumentException("Supply a fresh isolated data directory.");
        var root = Path.GetFullPath(args[0]);
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new InvalidOperationException("Fresh data directory required.");
        var live = args.Contains("--live-ui");
        var aliasIndex = Array.IndexOf(args, "--live-ui") + 1;
        if (live && aliasIndex >= args.Length) throw new ArgumentException("Pass the explicitly authorized SSH alias after --live-ui.");
        Environment.SetEnvironmentVariable("VPSNODE_DATA", root);
        _store = new Store(root);
        _store.Save(new Settings { Theme = "dark", IntervalMinutes = 1440, V2Directory = Path.Combine(root, "missing-core") });
        var app = new App(); app.InitializeComponent();
        _window = new MainWindow { Title = "VPSnode in-process form test", ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000 };
        app.MainWindow = _window;
        _window.Show();
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", Private)!.GetValue(_window)!).Stop();
        try
        {
            CheckAddAndRefresh(live, live ? args[aliasIndex] : "");
            CheckBackupForms(root);
            CheckComboBoxes();
            File.WriteAllLines(Path.Combine(root, "form-test-results.txt"), Results);
            return 0;
        }
        finally
        {
            foreach (var owned in _window.OwnedWindows.OfType<Window>().ToArray()) owned.Close();
            typeof(MainWindow).GetField("_closing", Private)!.SetValue(_window, true);
            _window.Close();
            app.Shutdown();
        }
    }

    private static void CheckAddAndRefresh(bool live, string alias)
    {
        using var cancelServer = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        Host entry;
        if (live)
        {
            // Read only the authorized existing SSH entry; never display its credential/path values.
            entry = Task.Run(() => SshInventory.FromAlias(alias, CancellationToken.None)).GetAwaiter().GetResult();
            Require(File.Exists(entry.KeyPath), "authorized SSH entry resolves an existing original key file");
        }
        else
        {
            listener.Start();
            entry = new Host { Id = "fixture", Name = "本机表单测试", Address = "127.0.0.1", User = "fixtureuser", SshPort = ((IPEndPoint)listener.LocalEndpoint).Port };
            _ = Task.Run(async () =>
            {
                try { while (!cancelServer.IsCancellationRequested) { using var connection = await listener.AcceptTcpClientAsync(cancelServer.Token); } }
                catch (OperationCanceledException) { }
                catch (SocketException) when (cancelServer.IsCancellationRequested) { }
            });
        }
        try
        {
            Call("Go", "hosts"); Layout();
            Modal("通过地址添加 VPS", () => Click(Button(_window, "添加 VPS")), dialog =>
            {
                Click(Button(dialog, "保存"));
                Require(dialog.IsVisible && Texts(dialog).Any(t => t.Contains("请填写服务器 IP 或域名")), "Add VPS blocks blank address without saving");
                Input(dialog, "服务器 IP 或域名（必填）").Text = entry.Address;
                Input(dialog, "SSH 登录账号（留空沿用 OpenSSH 配置）").Text = entry.User;
                Input(dialog, "SSH 端口").Text = "70000";
                Click(Button(dialog, "保存"));
                Require(dialog.IsVisible && Texts(dialog).Any(t => t.Contains("SSH 端口须为")), "Add VPS validates SSH port in the form");
                Input(dialog, "SSH 端口").Text = entry.SshPort.ToString();
                Input(dialog, "私钥文件路径（已有默认密钥可留空）").Text = entry.KeyPath;
                Input(dialog, "显示名称（选填，默认用地址）").Text = live ? "授权 VPS 表单验收" : entry.Name;
                Click(Button(dialog, "保存"));
            });
            var host = _store.Hosts.Single();
            Require(host.Address == entry.Address && host.SshPort == entry.SshPort && host.User == entry.User, "Add VPS Save event persists entered endpoint and account");
            PumpUntil(() => typeof(MainWindow).GetField("_current", Private)!.GetValue(_window) is null, TimeSpan.FromSeconds(55));
            var first = _store.Latest().GetValueOrDefault(("host", host.Id));
            Require(first is not null, "Add VPS automatically enters Monitor and records SSH inventory result");
            if (live)
            {
                Require(first!.State == "SSH 可登录" && _store.Hosts.Single().InventoriedAt.HasValue, "live Add VPS completes read-only SSH inventory");
                Require(_store.Nodes.Length > 0 && _store.Nodes.All(n => n.HostId == host.Id && n.VpsVerified && n.ClientId.Length > 0), "live Add VPS persists collected client identities and VPS associations");
            }
            else Require(first!.State == "SSH 连接失败", "local fixture reports failed SSH honestly instead of configured or usable");
            var before = _store.Nodes.Select(n => n.Id).Order().ToArray();
            var beforeInventory = _store.Hosts.Single().InventoriedAt;
            Layout(); Click(Button(_window, "一键刷新"));
            PumpUntil(() => typeof(MainWindow).GetField("_current", Private)!.GetValue(_window) is null, TimeSpan.FromSeconds(55));
            var second = _store.Latest()[ ("host", host.Id) ];
            Require(second.At > first!.At, "Refresh button runs a new SSH observation");
            Require(_store.Hosts.Length == 1 && _store.Nodes.Select(n => n.Id).Order().SequenceEqual(before), "Add then Refresh preserves asset IDs without duplicate records");
            Require(_store.History("task", "history", DateTimeOffset.MinValue).Select(o => Wire.Decode<Inspection>(o.Detail)).Count(t => t.Kind == "inventory") == 2,
                "Add and Refresh both persist task-center inventory results");
            if (live)
            {
                Require(second.State == "SSH 可登录" && _store.Hosts.Single().InventoriedAt > beforeInventory, "live Refresh updates the actual inventory timestamp");
                var sites = WebProbe.Targets(_store);
                Require(sites.Any(w => w.AutoFallbackNodeIds.Length > 0), "live Add and Refresh expose evidence-based automatic fallback association");
            }
        }
        finally { cancelServer.Cancel(); listener.Stop(); }
    }

    private static void CheckBackupForms(string root)
    {
        Call("Go", "settings"); Layout();
        Require(Button(_window, "导出全部数据") is not null && Button(_window, "导入数据") is not null, "Settings exposes both backup entry buttons");
        object? returned = null;
        const string password = "fixture-only-backup-18";
        Modal("加密导出", () => returned = Call("BackupPassword", true), dialog =>
        {
            var fields = Walk(dialog).OfType<PasswordBox>().ToArray();
            fields[0].Password = "short"; fields[1].Password = "short";
            Click(Button(dialog, "继续"));
            Require(dialog.IsVisible && Texts(dialog).Any(t => t.Contains("至少 12")), "backup form rejects short password");
            fields[0].Password = password; fields[1].Password = password + "wrong";
            Click(Button(dialog, "继续"));
            Require(dialog.IsVisible && Texts(dialog).Any(t => t.Contains("两次口令不一致")), "backup form rejects mismatched confirmation");
            fields[1].Password = password;
            Click(Button(dialog, "继续"));
        });
        Require(returned as string == password, "backup form returns only confirmed password");
        var file = Path.Combine(root, "fixture-form.vpsbackup");
        _store.ExportBackup(file, password);
        var preview = _store.PreviewBackup(file, password);
        var before = _store.Hosts.Length;
        Modal("确认导入数据", () => returned = Call("ConfirmBackupImport", preview), dialog =>
        {
            Require(Texts(dialog).Any(t => t.Contains($"VPS {preview.Hosts}") && t.Contains($"节点 {preview.Nodes}")), "import preview renders counts from decrypted backup");
            var radios = Walk(dialog).OfType<RadioButton>().ToArray();
            Require(radios.Single(r => r.Content.ToString()!.StartsWith("合并")).IsChecked == true, "import preview defaults to merge");
            radios.Single(r => r.Content.ToString()!.StartsWith("替换")).IsChecked = true;
            Click(Button(dialog, "导入"));
            Require(dialog.IsVisible && Texts(dialog).Any(t => t.Contains("替换会覆盖现有记录")), "replacement requires explicit backup acknowledgement");
            Walk(dialog).OfType<CheckBox>().Single(c => c.Content.ToString()!.Contains("确认完整替换")).IsChecked = true;
            Click(Button(dialog, "导入"));
        });
        Require(returned is true && _store.Hosts.Length == before, "preview returns replacement choice and does not mutate the store");
        Modal("确认导入数据", () => returned = Call("ConfirmBackupImport", preview), dialog => Click(Button(dialog, "取消")));
        Require(returned is null && _store.Hosts.Length == before, "canceling backup preview leaves ledger unchanged");
        Modal("确认导入数据", () => returned = Call("ConfirmBackupImport", preview), dialog => Click(Button(dialog, "导入")));
        Require(returned is false, "merge preview confirms without destructive replacement acknowledgement");
    }

    private static void CheckComboBoxes()
    {
        Call("Go", "nodes"); Layout();
        var combo = Walk(_window).OfType<ComboBox>().Single();
        Require(Equals(combo.Foreground, Application.Current.FindResource("Text")) && Equals(combo.Background, Application.Current.FindResource("Surface")), "ComboBox uses current theme text and surface");
        combo.IsDropDownOpen = true; Layout();
        Require(combo.Template.FindName("PART_Popup", combo) is System.Windows.Controls.Primitives.Popup { IsOpen: true }, "themed selector opens its real dropdown popup");
        combo.IsDropDownOpen = false;
        Modal("从 SSH 别名导入", () => Call("ImportAlias"), dialog =>
        {
            var editable = Walk(dialog).OfType<ComboBox>().Single();
            editable.ApplyTemplate();
            Require(editable.Template.FindName("PART_EditableTextBox", editable) is TextBox { Visibility: Visibility.Visible }, "editable SSH alias retains its text input part");
            editable.Text = "fixture-only-alias";
            Require(editable.Text == "fixture-only-alias", "editable SSH alias accepts typed configuration text");
            Click(Button(dialog, "取消"));
        });
    }

    private static object? Call(string method, params object?[] args) => typeof(MainWindow).GetMethod(method, Private)!.Invoke(_window, args);
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
    private static Button Button(DependencyObject root, string text) => Walk(root).OfType<Button>().Single(b => Equals(b.Content, text));
    private static TextBox Input(DependencyObject root, string name) => Walk(root).OfType<TextBox>().Single(b => AutomationProperties.GetName(b) == name);
    private static IEnumerable<string> Texts(DependencyObject root) => Walk(root).OfType<TextBlock>().Select(t => t.Text);
    private static void Layout()
    {
        _window.UpdateLayout();
        _window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static void Modal(string title, Action open, Action<Window> interact)
    {
        Exception? failure = null; var handled = false;
        var watch = Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            var dialog = _window.OwnedWindows.OfType<Window>().FirstOrDefault(w => w.IsVisible && w.Title == title);
            if (dialog is null)
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Expected modal did not open: " + title);
                return;
            }
            timer.Stop(); handled = true;
            try { dialog.UpdateLayout(); interact(dialog); }
            catch (Exception ex) { failure = ex; dialog.Close(); }
        };
        timer.Start();
        try { open(); }
        finally { timer.Stop(); }
        if (failure != null) throw new InvalidOperationException("Form assertion failed: " + title, failure);
        Require(handled, "actual application modal opened: " + title);
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > timeout) throw new TimeoutException("Application task did not complete within test timeout.");
            _window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Thread.Sleep(10);
        }
        Layout();
    }

    private static IEnumerable<DependencyObject> Walk(DependencyObject item)
    {
        yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(item, i))) yield return child;
    }

    private static void Require(bool result, string message)
    {
        if (!result) throw new InvalidOperationException(message);
        Results.Add("PASS " + message); Console.WriteLine("PASS " + message);
    }
}
