using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using VPSnode.Core;
using VPSnode.Desktop;

// Opt-in loopback integration: uses an existing v2rayN Xray executable, never its user configuration.
internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh isolated data directory required.");
        var directory = Environment.GetEnvironmentVariable("VPSNODE_TEST_V2RAYN_DIRECTORY") ?? "";
        var core = ProxyProbe.FindCore(directory, "xray") ?? throw new InvalidOperationException("Set VPSNODE_TEST_V2RAYN_DIRECTORY to an existing v2rayN install.");
        Environment.SetEnvironmentVariable("VPSNODE_DATA", Path.GetFullPath(args[0]));
        var store = new Store();
        using var cancel = new CancellationTokenSource();
        using var http = new TcpListener(IPAddress.Loopback, 0); http.Start();
        var webPort = ((IPEndPoint)http.LocalEndpoint).Port;
        var received = 0;
        var server = Task.Run(async () =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    using var client = await http.AcceptTcpClientAsync(cancel.Token);
                    using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    var request = await reader.ReadLineAsync(cancel.Token) ?? "";
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync(cancel.Token))) { }
                    Interlocked.Increment(ref received);
                    await Task.Delay(300, cancel.Token);
                    var download = request.Contains("/download");
                    var body = download ? new byte[512_000] : [];
                    var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(download ? "200 OK" : "204 No Content")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, cancel.Token);
                    if (download) await stream.WriteAsync(body, cancel.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) when (cancel.IsCancellationRequested) { }
        });
        using var reserve = new TcpListener(IPAddress.Loopback, 0); reserve.Start();
        var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        const string id = "00000000-0000-4000-8000-000000000019";
        var config = JsonSerializer.Serialize(new {
            log = new { loglevel = "none" },
            inbounds = new[] { new { listen = "127.0.0.1", port, protocol = "vless",
                settings = new { clients = new[] { new { id } }, decryption = "none" } } },
            // Xray's server default blocks loopback. Permit only this fixture's TCP origin.
            // https://xtls.github.io/en/config/outbounds/freedom.html#finalruleobject
            outbounds = new[] { new { protocol = "freedom", settings = new {
                finalRules = new[] { new { action = "allow", network = "tcp", ip = new[] { "127.0.0.1" }, port = webPort } }
            } } }
        });
        var info = new ProcessStartInfo(core) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "run", "-config", "stdin:" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        process.StandardInput.Write(config); process.StandardInput.Close();
        _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        var app = new App(); app.InitializeComponent();
        MainWindow? window = null;
        try
        {
            for (int i = 0; i < 100; i++)
            {
                try { using var probe = new TcpClient(); probe.Connect(IPAddress.Loopback, port); break; }
                catch (SocketException) { Thread.Sleep(50); }
            }
            store.Save(new Settings { V2Directory = directory, PingUrl = $"http://127.0.0.1:{webPort}/delay", PingStatus = 204,
                SpeedUrl = $"http://127.0.0.1:{webPort}/download", SpeedBytes = 256_000 });
            store.Save(new Node { Id = "loopback", Name = "虚构本地代理测试", Protocol = "vless", Transport = "tcp", Port = port,
                Address = "127.0.0.1", Link = $"vless://{id}@127.0.0.1:{port}?encryption=none&security=none&type=tcp#Fixture" });
            window = new MainWindow(); app.MainWindow = window; window.Show();
            ((DispatcherTimer)typeof(MainWindow).GetField("_timer", Private)!.GetValue(window)!).Stop();
            typeof(MainWindow).GetMethod("Go", Private)!.Invoke(window, ["nodes"]); window.UpdateLayout();
            foreach (var (buttonName, kind, expected) in new[] { ("测延迟", "node", "可用"), ("测下载", "speed", "完成") })
            {
                var button = Walk(window).OfType<Button>().Single(b => Equals(b.Content, buttonName));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
                Check(Walk(window).OfType<Button>().Where(b => b.Content is "测延迟" or "测下载").All(b => !b.IsEnabled), "duplicate node actions disabled during " + kind);
                var watch = Stopwatch.StartNew();
                while (typeof(MainWindow).GetField("_current", Private)!.GetValue(window) != null)
                {
                    if (watch.Elapsed > TimeSpan.FromSeconds(50)) throw new TimeoutException("Node action timed out.");
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); Thread.Sleep(10);
                }
                var result = store.Latest()[(kind, "loopback")];
                Console.WriteLine($"Fixture {kind}: {result.State} · {result.Detail}; origin requests {received}; server exited {process.HasExited}");
                Check(result.State == expected && result.Value > 0, "actual Xray proxy " + kind + " through list button");
                if (kind == "speed") Check(result.Detail.StartsWith("256,000") || result.Detail.StartsWith("256.000") || result.Detail.StartsWith("256 000"), "download byte cap observed");
            }
            Check(received == 3, "two latency requests and one download reached loopback origin");
            Check(store.History("task", "history", DateTimeOffset.MinValue).Length == 2, "both actions recorded in task center");
            Check(!store.Settings.ScheduledInspectionEnabled, "manual proxy actions work with scheduled inspection disabled");
        }
        finally
        {
            if (window != null) { typeof(MainWindow).GetField("_closing", Private)!.SetValue(window, true); window.Close(); }
            app.Shutdown(); cancel.Cancel(); http.Stop();
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(); }
            try { server.GetAwaiter().GetResult(); } catch (SocketException) { }
        }
    }
    private static IEnumerable<DependencyObject> Walk(DependencyObject item)
    {
        yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(item); i++)
            foreach (var child in Walk(VisualTreeHelper.GetChild(item, i))) yield return child;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); Console.WriteLine("PASS " + message); }
}
