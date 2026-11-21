using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VPSnode.Core;

if (args.Length != 1 || Directory.Exists(args[0])) throw new ArgumentException("Fresh isolated directory required.");
var root = Path.GetFullPath(args[0]); Environment.SetEnvironmentVariable("VPSNODE_DATA", root);
var repo = new DirectoryInfo(AppContext.BaseDirectory);
while (!File.Exists(Path.Combine(repo.FullName, "VPSnode.slnx"))) repo = repo.Parent ?? throw new Exception("Repository unavailable.");
var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
var cli = Path.Combine(repo.FullName, "src", "VPSnode.Cli", "bin", configuration, "net10.0-windows", "vpsnode-cli.exe");
var store = new Store(); int checks = 0;
JsonElement Run(string[] command, object? data = null, int expected = 0)
{
    var info = new ProcessStartInfo(cli) { UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
    foreach (var word in command) info.ArgumentList.Add(word);
    using var child = Process.Start(info)!;
    if (data != null) child.StandardInput.Write(data is string text ? text : Wire.Encode(data));
    child.StandardInput.Close();
    var output = child.StandardOutput.ReadToEndAsync(); var errors = child.StandardError.ReadToEndAsync();
    if (!child.WaitForExit(20000)) { child.Kill(true); throw new TimeoutException(string.Join(' ', command)); }
    var value = JsonDocument.Parse(output.GetAwaiter().GetResult()).RootElement.Clone();
    Check(child.ExitCode == expected && value.GetProperty("protocolVersion").GetInt32() == 1 && errors.GetAwaiter().GetResult().Length == 0,
        string.Join(' ', command) + " exit/JSON");
    return value;
}
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; Console.WriteLine("PASS " + message); }

Run(["help"]); Run(["version"]);
Run(["asset", "add", "host", "h"], new { name = "示例 VPS", address = "192.0.2.10", sshAlias = "fixture", user = "root", sshPassword = "fictional-password", ports = new[] { new Listener("TCP", "0.0.0.0", 443, "fixture") } });
var host = Run(["asset", "get", "host", "h"]);
Check(host.GetProperty("sshPasswordSet").GetBoolean() && !host.GetRawText().Contains("fictional-password"), "SSH credential protected and port inventory queryable");
Run(["asset", "add", "host", "h"], new { name = "重复" }, 4);
Run(["asset", "update", "host", "h", "--revision", host.GetProperty("revision").GetString()!], new { name = "CLI 更新", notes = "说明" });
Run(["asset", "update", "host", "h", "--revision", host.GetProperty("revision").GetString()!], new { name = "stale" }, 4);
Run(["asset", "update", "host", "h"], new { sshPort = 0 }, 2);
Run(["asset", "update", "host", "h"], new { typo = true }, 2);
Run(["asset", "update", "host", "h"], "{\"notes\":null}", 2);
Run(["asset", "update", "host", "h"], "{\"notes\":\"x\",\"Notes\":\"y\"}", 2);
Run(["asset", "update", "host", "h"], "{", 2);
Run(["asset", "get", "host", "missing"], expected: 3);
Run(["asset", "add", "node", "n"], new { name = "示例节点", hostId = "h", link = "vless://00000000-0000-4000-8000-000000000011@192.0.2.10:443?type=tcp&security=tls&sni=site.example.org", paused = true });
var node = Run(["node", "config", "n"]);
Check(node.GetProperty("asset").GetProperty("protocol").GetString() == "VLESS" && !node.GetRawText().Contains("00000000-0000-4000-8000-000000000011"), "normalized configuration and default redaction");
Check(Run(["node", "config", "n", "--include-secrets"]).GetRawText().Contains("00000000-0000-4000-8000-000000000011"), "explicit configuration reveal");
Run(["node", "export", "n"], expected: 2); Run(["node", "export", "n", "--include-secrets"]);
Run(["node", "resume", "n"]); Check(!store.Nodes.Single().Paused, "resume persisted"); Run(["node", "pause", "n"]);
Run(["asset", "add", "web", "w"], new { name = "示例站点", hostId = "h", domain = "site.example.org", certificateNodeIds = new[] { "n" }, fallbackNodeIds = new[] { "n" } });
Run(["asset", "update", "web", "w"], new { fallbackNodeIds = Array.Empty<string>(), enabled = false });
Check(store.Websites.Single().FallbackNodeIds.Length == 0 && !store.Websites.Single().Enabled, "empty relation array and false are explicit updates");
Run(["settings", "set"], new { intervalMinutes = 22, historyDays = 45, speedBytes = 1234567, followGroups = new[] { "fixture" } });
Run(["settings", "set"], new { intervalMinutes = 0 }, 2); Run(["settings", "set"], new { theme = "invalid" }, 2);
Check(store.Settings.IntervalMinutes == 22 && store.Settings.SpeedBytes == 1234567, "invalid settings do not partially save");
var oldHost = store.Hosts.Single(); store.Save(oldHost with { Notes = "GUI equivalent update" }, oldHost);
Check(Run(["asset", "get", "host", "h"]).GetProperty("asset").GetProperty("notes").GetString() == "GUI equivalent update", "CLI reads another connection's committed changes");
try { store.Save(oldHost with { Notes = "lost update" }, oldHost); throw new Exception("missing conflict"); }
catch (InvalidDataException) { Check(true, "GUI optimistic edit rejects stale snapshot"); }
var task = Run(["check", "node", "n"] ).GetProperty("task"); var id = task.GetProperty("id").GetString()!;
Check(task.GetProperty("state").GetString() is "queued" or "running" or "failed", "submission does not report success");
var final = Run(["task", "wait", id], expected: 5);
Check(final.GetProperty("state").GetString() == "failed" && final.GetProperty("result").GetProperty("items")[0].GetProperty("detail").GetString() == "已暂停", "no GUI: worker final result and reason");
Run(["task", "get", id]); Run(["task", "list"]); Run(["history", "task", "history"]); Run(["check", "node", "missing"], expected: 3);
var queued = store.SubmitTask("node", "n");
Check(store.SubmitTask("node", "n").Id == queued.Id, "active submissions deduplicate by stable ID");
Run(["task", "cancel", queued.Id]); Check(store.GetTask(queued.Id)!.State == "canceled", "queued cancel persisted");
Run(["task", "wait", queued.Id], expected: 6);
var interrupted = store.SubmitTask("node", "n");
using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(root, "vpsnode.db")}"))
{
    db.Open(); using var q = db.CreateCommand(); q.CommandText = "UPDATE assets SET data=$d WHERE kind='inspection-task' AND id=$i";
    var bytes = System.Text.Encoding.UTF8.GetBytes(Wire.Encode(interrupted with { State = "running" }));
    q.Parameters.AddWithValue("$d", System.Security.Cryptography.ProtectedData.Protect(bytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    q.Parameters.AddWithValue("$i", interrupted.Id); q.ExecuteNonQuery();
}
Run(["worker"]); Check(store.GetTask(interrupted.Id)!.ErrorCode == "executor_interrupted", "abandoned executor has durable failure reason");
var at = DateTimeOffset.Now.AddDays(-10);
store.Record(new("node", "n", at, "可用", Value: 80));
Run(["history", "node", "n", "--since", at.AddDays(-1).ToString("O")]);
Check(ObservationState.Current(new("node", "n", at, "可用")) == "可用", "old node observation remains successful");
Check(ObservationState.Current(new("tls", "w", at, "TLS 正常", Expires: DateTimeOffset.Now.AddMinutes(-1))) == "证书已过期", "real expiration remains dynamic");
Check(ObservationState.Current(new("tls", "w", at, "TLS 正常", Expires: DateTimeOffset.Now.AddDays(40))) == "TLS 正常", "old TLS observation does not decay");
var backup = Path.Combine(root, "fixture.vpsbackup"); var password = new { password = "fictional-backup-password" };
var backupQueued = store.SubmitTask("node", "n");
Run(["backup", "export", backup], password);
Check(Run(["backup", "preview", backup], password).GetProperty("requests").GetInt32() == 1, "backup preview counts durable pending tasks");
Run(["backup", "preview", backup], new { password = "wrong" }, 2);
Run(["asset", "delete", "node", "n"]); Check(store.Websites.Single().CertificateNodeIds.Length == 0, "delete cleans relations");
Run(["backup", "restore", backup, "--mode", "replace"], password);
Check(store.Nodes.Single().Id == "n" && store.GetTask(id)!.Terminal, "encrypted restore includes durable task receipts");
store.CancelTask(backupQueued.Id);
Run(["asset", "list", "host"]); Run(["asset", "list", "node"]); Run(["asset", "list", "web"]); Run(["status"]); Run(["data", "info"]);
Run(["node", "duplicates"]); Run(["history", "node", "n", "--limit", "-1"], expected: 2); Run(["version", "--typo"], expected: 2);
var v2 = Path.Combine(root, "fictional-v2rayn"); Directory.CreateDirectory(Path.Combine(v2, "guiConfigs"));
File.WriteAllText(Path.Combine(v2, "v2rayN.exe"), "fictional marker, never executed");
File.WriteAllText(Path.Combine(v2, "guiConfigs/guiNConfig.json"), "{\"SpeedTestItem\":{\"SpeedPingTestUrl\":\"https://probe.example.org/204\"}}");
using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={V2Sync.Database(v2)}"))
{
    db.Open(); using var q = db.CreateCommand();
    q.CommandText = """
        CREATE TABLE SubItem(Id TEXT, Remarks TEXT);
        INSERT INTO SubItem VALUES('g','示例分组');
        CREATE TABLE ProfileItem(IndexId TEXT, Subid TEXT, Address TEXT, ConfigType INTEGER, Port INTEGER, Id TEXT, Remarks TEXT);
        INSERT INTO ProfileItem VALUES('source-a','g','192.0.2.25',5,443,'00000000-0000-4000-8000-000000000025','示例来源节点');
        """; q.ExecuteNonQuery();
}
Run(["settings", "set"], new { v2Directory = v2, followGroups = new[] { "g" } });
Check(Run(["v2rayn", "info"]).GetProperty("groups").GetArrayLength() == 2, "reads isolated v2rayN grouped and ungrouped entries");
Run(["v2rayn", "sync", "--groups", "g"]); Check(store.Nodes.Any(n => n.V2Id == "source-a"), "v2rayN synchronization uses Core provenance");
Run(["v2rayn", "sync", "--groups", "@ungrouped"]); Run(["v2rayn", "sync", "--groups", "missing"], expected: 2);
Run(["settings", "read-v2rayn-target", "--apply"]); Check(store.Settings.PingUrl == "https://probe.example.org/204", "v2rayN target read and saved");
Run(["node", "import-links", "--preview"], new { links = new[] { "vless://00000000-0000-4000-8000-000000000033@192.0.2.33:443?security=none" } });
Check(!store.Nodes.Any(n => n.Address == "192.0.2.33"), "share preview makes no changes");
Run(["node", "import-links"], new { links = new[] { "vless://00000000-0000-4000-8000-000000000033@192.0.2.33:443?security=none" } });
Run(["worker"]);
Run(["node", "associate", "n", store.Nodes.Single(n => n.Address == "192.0.2.33").Id], expected: 2);
Run(["asset", "add", "host", "closed-ssh"], new { name = "回环失败样例", address = "127.0.0.1", sshPort = 1 });
Check(Run(["check", "host", "closed-ssh", "--wait"], expected: 5).GetProperty("state").GetString() == "failed", "real OpenSSH connection failure is reported");
Run(["check", "sni", "n", "--wait"], expected: 5);
using var fixtureStop = new CancellationTokenSource();
using var httpServer = new TcpListener(IPAddress.Loopback, 0); httpServer.Start();
var httpPort = ((IPEndPoint)httpServer.LocalEndpoint).Port;
var httpLoop = Task.Run(async () => {
    try {
        while (!fixtureStop.IsCancellationRequested) {
            using var client = await httpServer.AcceptTcpClientAsync(fixtureStop.Token);
            using var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(fixtureStop.Token))) { }
            var body = "<html><title>Fictional fixture</title>fixture</html>";
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"), fixtureStop.Token);
        }
    } catch (OperationCanceledException) { }
});
try {
    Run(["asset", "add", "web", "http-fixture"], new { name = "HTTP 回环样例", domain = "127.0.0.1", port = 1, url = $"http://127.0.0.1:{httpPort}/", checkWebsite = false });
    Run(["check", "web", "http-fixture", "--wait"], expected: 5);
    Check(store.Latest()[("web", "http-fixture")].HttpStatus == 200 && store.Latest()[("web", "http-fixture")].Title == "Fictional fixture", "actual CLI website records HTTP/title independently of TLS failure");
} finally {
    fixtureStop.Cancel(); httpServer.Stop();
    httpLoop.GetAwaiter().GetResult();
}
Console.WriteLine($"PASS {checks} Agent/CLI checks (actual child processes; no production data)");
