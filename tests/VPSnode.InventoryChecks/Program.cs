using System.Diagnostics;
using System.Text.Json;
using VPSnode.Core;

if (args.Length == 0) throw new ArgumentException("Supply a fresh data directory; optionally SSH alias and v2rayN directory for read-only live checks.");
if (Directory.Exists(args[0])) throw new ArgumentException("Data directory must be new.");
var store = new Store(args[0]);
void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); Console.WriteLine("PASS " + name); }
var fake = ShareLink.New("vless://11111111-1111-1111-1111-111111111111@site.example:443?type=tcp&security=tls&sni=site.example", "h", "n") with
{
    VpsVerified = true, Server = new()
    {
        ["配置状态"] = "面板已配置",
        ["回落"] = "[{\"dest\":\"127.0.0.1:80\",\"name\":\"\",\"path\":\"\",\"alpn\":\"\",\"xver\":0}]",
        ["原始传输设置"] = "{\"security\":\"tls\",\"tlsSettings\":{\"serverName\":\"site.example\",\"alpn\":[\"http/1.1\"]}}"
    }
};
var site = new WebTarget { Id = "w", Name = "example", Domain = "site.example", HostId = "h" };
Check(WebProbe.ConfiguredFallback(fake, site), "explicit TLS endpoint + server fallback associates");
Check(!WebProbe.ConfiguredFallback(fake with { Server = new() { ["分享地址"] = "site.example" } }, site), "share address alone never proves fallback");
Check(!WebProbe.ConfiguredFallback(fake with { Server = new(fake.Server) { ["回落"] = "[{\"dest\":80,\"name\":\"other.example\"}]" } }, site), "different fallback SNI does not associate");
Check(!WebProbe.ConfiguredFallback(fake with { Server = new(fake.Server) { ["回落"] = "[{\"dest\":80,\"path\":\"/private\"}]" } }, site), "path-specific rule does not prove root website fallback");
Check(!WebProbe.ConfiguredFallback(fake with { Server = new(fake.Server) { ["配置状态"] = "本次未发现；保留历史" } }, site), "stale fallback snapshot does not auto-confirm current relationship");
Check(!WebProbe.ConfiguredFallback(fake with { Server = new(fake.Server) { ["回落"] = "[{\"dest\":80,\"alpn\":\"h2\"}]" } }, site), "mismatched ALPN does not associate");
store.Save(new Host { Id = "h", Name = "example" }); store.Save(fake);
store.Save(site); store.Save(site with { Id = "w2", HostId = "" });
Check(WebProbe.Targets(store).Length == 1 && WebProbe.Targets(store)[0].FallbackNodeIds.Contains("n"), "hostless and assigned identical endpoint displays once");
Check(store.Websites.Length == 2, "duplicate view consolidation preserves registrations and history IDs");
var derived = WebProbe.Targets(store)[0];
Check(derived.AutoFallbackNodeIds.SequenceEqual(["n"]), "view distinguishes derived fallback from manual registration");
store.Save(derived with { Name = "edited name" });
Check(store.Websites.All(w => !w.FallbackNodeIds.Contains("n")), "editing derived website does not persist an automatic fallback as manual");
store.Save(fake with { Server = new(fake.Server) { ["回落"] = "[]" } });
Check(WebProbe.Targets(store).All(w => !w.FallbackNodeIds.Contains("n")), "removing remote fallback removes automatic relation after an edit");
store.Save(site with { FallbackNodeIds = ["n"] });
Check(WebProbe.Targets(store).Any(w => w.FallbackNodeIds.Contains("n") && !w.AutoFallbackNodeIds.Contains("n")), "explicit manual fallback remains distinct and retained");
if (args.Length < 3) return;
var live = new Store(Path.Combine(args[0], "live"));
live.Save(new Settings { V2Directory = args[2], SpeedBytes = 512000 });
var host = await SshInventory.FromAlias(args[1], default); live.Save(host);
var first = await SshInventory.Probe(live, host, true, default);
Console.WriteLine(first.State + " " + first.Detail);
Check(first.State == "SSH 可登录" && live.Nodes.Length > 0, "live add and inventory");
var ids = live.Nodes.Select(n => n.Id).Order().ToArray();
var second = await SshInventory.Probe(live, host, true, default);
Check(second.State == "SSH 可登录" && ids.SequenceEqual(live.Nodes.Select(n => n.Id).Order()), "live refresh is idempotent");

// Independent SQL read: compare sensitive values in memory, emit only equality and counts.
var script = """
    import sqlite3,json
    c=sqlite3.connect('file:/etc/x-ui/x-ui.db?mode=ro',uri=True);c.row_factory=sqlite3.Row
    rows=[]
    for i in c.execute('select * from inbounds'):
      for r in c.execute('select c.uuid,c.flow,ci.flow_override from clients c join client_inbounds ci on c.id=ci.client_id where ci.inbound_id=?',(i['id'],)):
        fallbacks=[{k:f[k] for k in ['dest','name','alpn','path','xver']} for f in c.execute('select * from inbound_fallbacks where master_id=? order by sort_order,id',(i['id'],))]
        rows.append({'inbound':str(i['id']),'uuid':r['uuid'],'flow':r['flow_override'] if r['flow_override'] is not None else r['flow'],'fallbacks':fallbacks,'stream':json.loads(i['stream_settings']),'settings':json.loads(i['settings'])})
    print(json.dumps(rows))
    """;
var psi = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe"))
{ UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
foreach (var arg in new[] { "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=8", "--", args[1], "python3 -" }) psi.ArgumentList.Add(arg);
using var process = Process.Start(psi)!;
var outputTask = process.StandardOutput.ReadToEndAsync(); var errorTask = process.StandardError.ReadToEndAsync();
await process.StandardInput.WriteAsync(script); process.StandardInput.Close();
await process.WaitForExitAsync(); var raw = await outputTask; await errorTask;
Check(process.ExitCode == 0, "independent SQL read completed");
using var json = JsonDocument.Parse(raw);
Check(json.RootElement.GetArrayLength() == live.Nodes.Length, "ledger client count equals independent DB read");
foreach (var record in json.RootElement.EnumerateArray())
{
    var node = live.Nodes.Single(n => n.InboundId == record.GetProperty("inbound").GetString() && n.ClientId == record.GetProperty("uuid").GetString());
    var link = ShareLink.Parse(node.Link);
    Check(link.Auth == record.GetProperty("uuid").GetString() && link.Option("flow") == record.GetProperty("flow").GetString(), "client auth and flow equal DB for inbound " + node.InboundId);
    using var actual = JsonDocument.Parse(node.Server["回落"]);
    var expected = record.GetProperty("fallbacks");
    Check(JsonElement.DeepEquals(actual.RootElement, expected), "fallback fields equal independent table for inbound " + node.InboundId);
    using var stream = JsonDocument.Parse(node.Server["原始传输设置"]);
    Check(JsonElement.DeepEquals(stream.RootElement, record.GetProperty("stream")), "full transport snapshot equals DB for inbound " + node.InboundId);
    Check(link.Option("encryption") == record.GetProperty("settings").GetProperty("encryption").GetString(), "client encryption equals DB for inbound " + node.InboundId);
    Console.WriteLine("Evidence inbound " + node.InboundId + ": " + node.Server["配置比对"] + " / " + node.Server["进程监听"]);
}
var websites = WebProbe.Targets(live);
Check(websites.Length == 1 && websites[0].FallbackNodeIds.Length == 1, "live TLS website occurs once and fallback associates");
if (args.Contains("--inventory-only")) return;
foreach (var node in live.Nodes)
{
    var latency = await ProxyProbe.Run(live, node, false, default); live.Record(latency);
    Console.WriteLine($"Probe inbound {node.InboundId}: {latency.State}, {latency.Value:0.#} ms, {latency.Detail}");
    Check(latency.State == "可用", "live proxy latency " + node.InboundId);
    var speed = await ProxyProbe.Run(live, node, true, default); live.Record(speed);
    Console.WriteLine($"Download inbound {node.InboundId}: {speed.State}, {speed.Value:0.#} Mbps, {speed.Detail}");
    Check(speed.State == "完成", "live capped download " + node.InboundId);
}
foreach (var result in await WebProbe.Run(websites[0], true, default))
{
    live.Record(result); Console.WriteLine($"Web {result.Kind}: {result.State}, {result.Title}, {result.Detail}");
}
