using Microsoft.Data.Sqlite;
using VPSnode.Core;

if (args.Length == 0) throw new ArgumentException("Supply a fresh isolated data directory.");
var root = Path.GetFullPath(args[0]);
if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any()) throw new ArgumentException("Directory must be empty.");
var passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
const string id = "00000000-0000-4000-8000-000000000001";
const string secondId = "00000000-0000-4000-8000-000000000002";
const string link = "vless://" + id + "@192.0.2.10:443?type=tcp&security=tls&sni=site.example.org&encryption=none#Fixture";
var host = new Host { Id = "fixture", Name = "Fixture", Address = "192.0.2.10" };
var first = ShareLink.New(link, host.Id, "first") with { InboundId = "1", ClientId = id, VpsVerified = true };
var same = ShareLink.New(link.Replace("Fixture", "Another").Replace("type=tcp", "type=raw"), "", "same");
Check(NodeIdentity.Compare(first, same, [host]).Disposition == "Same", "remark and TCP/RAW normalize without losing identity");
var partial = first with { Id = "partial", Link = "", Transport = "", Security = "", Server = [] };
Check(NodeIdentity.Compare(first, partial, [host]).Disposition == "Same", "missing collected fields remain unknown");
Check(NodeIdentity.Compare(first, partial with { ClientId = "" }, [host]).Disposition == "Review", "missing client identity requires confirmation");
var domain = ShareLink.New(link.Replace("@192.0.2.10", "@site.example.org"), "", "domain");
Check(NodeIdentity.Compare(first, domain, [host]).Disposition == "Review", "domain is not inferred from a matching UUID alone");
first = first with { Server = new() { ["分享地址"] = "site.example.org" } };
Check(NodeIdentity.Compare(first, domain, [host]).Disposition == "Same", "verified custom share address bridges domain and IP");
Check(NodeIdentity.Compare(first, same with { Link = same.Link.Replace(id, secondId) }, [host]).Disposition == "Different", "different clients of one inbound never merge");
var route = ShareLink.New(link.Replace("type=tcp", "type=xhttp") + "", host.Id, "route");
Check(NodeIdentity.Compare(first, route, [host]).Disposition == "Different", "different transports remain different nodes");
var xhttp = ShareLink.New(link.Replace("type=tcp", "type=xhttp&path=%2Fa"), host.Id, "xhttp");
var otherPath = ShareLink.New(link.Replace("type=tcp", "type=xhttp&path=%2Fb"), host.Id, "other-path");
Check(NodeIdentity.Compare(xhttp, otherPath, [host]).Disposition == "Review", "route conflict is explicit and never automatically merged");
Check(NodeIdentity.Compare(first, first with { IpMode = "ipv6" }, [host]).Disposition == "Review", "IP family mode remains a route distinction");
var wsServer = first with { Id = "ws-server", Link = "", Transport = "ws", Server = new()
{
    ["原始入站设置"] = "{\"decryption\":\"none\"}",
    ["原始客户端设置"] = "{\"id\":\"" + id + "\",\"flow\":\"\"}",
    ["原始传输设置"] = """{"network":"ws","security":"tls","tlsSettings":{"serverName":"actual.example.org"},"wsSettings":{"path":"/actual","headers":{"Host":"route.example.org"}}}"""
} };
var wsClient = ShareLink.New(link.Replace("type=tcp", "type=ws&path=%2Factual&host=route.example.org").Replace("site.example.org", "actual.example.org"), id: "ws-client");
Check(NodeIdentity.Compare(wsServer, wsClient, [host]).Disposition == "Same", "server route evidence matches a complete client even without a generated link");
var wrongWs = wsClient with { Link = wsClient.Link.Replace("%2Factual", "%2Fwrong").Replace("actual.example.org", "wrong.example.org") };
var wsConflict = NodeIdentity.Compare(wsServer, wrongWs, [host]);
Check(wsConflict.Disposition == "Review" && wsConflict.Differences.Any(d => d.Field == "sni" && d.Conflict) &&
    wsConflict.Differences.Any(d => d.Field == "path" && d.Conflict), "collected SNI and path conflicts cannot auto merge when server link is missing");
Check(NodeIdentity.Compare(wsServer, wsClient with { Link = wsClient.Link.Replace("type=ws", "type=ws&flow=xtls-rprx-vision") }, [host]).Disposition == "Review",
    "known empty server flow conflicts with a nonempty client flow");
Check(NodeIdentity.Compare(wsServer, wsClient with { Link = wsClient.Link.Replace("path=%2Factual&", "") }, [host]).Disposition == "Review",
    "complete WebSocket client with default path conflicts with a different collected server path");
var httpHeader = first with { Link = "", Server = new() { ["原始传输设置"] = """{"network":"tcp","tcpSettings":{"header":{"type":"http"}}}""" } };
Check(NodeIdentity.Compare(httpHeader, same, [host]).Disposition == "Review", "collected HTTP header cannot merge with plain TCP client defaults");
var realityServer = wsServer with { Transport = "tcp", Security = "reality", Server = new()
{
    ["原始传输设置"] = """{"network":"tcp","security":"reality","realitySettings":{"serverNames":["first.example.org","second.example.org"],"shortIds":["ab12","cd34"],"settings":{"publicKey":"fixture-key"}}}"""
} };
var realityClient = ShareLink.New(link.Replace("security=tls", "security=reality&sid=cd34&pbk=fixture-key").Replace("site.example.org", "second.example.org"), id: "reality-client");
Check(NodeIdentity.Compare(realityServer, realityClient, [host]).Disposition == "Same", "nonfirst allowed REALITY SNI and short ID match server alternatives");
Check(NodeIdentity.Compare(realityServer, realityClient with { Link = realityClient.Link.Replace("sid=cd34", "sid=ffff") }, [host]).Disposition == "Review",
    "REALITY short ID outside collected server alternatives requires review");
Check(NodeIdentity.Merge(partial, partial with { Port = 8443, Transport = "xhttp" }, true).Port == 8443,
    "server-only refreshed fields replace their older values");
var store = new Store(root);
store.Save(host); store.Save(first); store.Save(same);
var timestamp = DateTimeOffset.UtcNow;
store.Record(new("node", first.Id, timestamp, "可用", Value: 100));
store.Record(new("node", same.Id, timestamp, "超时"));
store.Record(new("speed", same.Id, timestamp, "完成", Value: 20));
store.Record(new("task", "history", timestamp, "完成", Wire.Encode(new Inspection("nodes", "手动", timestamp, "完成", [new("node", same.Id, same.Name, "成功", "")]))));
store.Save(new WebTarget { Id = "site", Name = "site", Domain = "site.example.org", FallbackNodeIds = [same.Id] });
store.Queue("speed", same.Id);
Check(store.ReconcileNodes() == 1 && store.Nodes.Length == 1, "reconcile historical duplicate");
var kept = store.Nodes.Single();
Check(store.History("node", kept.Id, DateTimeOffset.MinValue).Length == 2 && store.History("speed", kept.Id, DateTimeOffset.MinValue).Length == 1, "merge preserves both colliding observations and speed history");
Check(store.Websites.Single().FallbackNodeIds.Single() == kept.Id && store.Peek()!.TargetId == kept.Id, "merge remaps website and queued tests");
Check(Wire.Decode<Inspection>(store.History("task", "history", DateTimeOffset.MinValue).Single().Detail).Items.Single().Id == kept.Id, "merge remaps task history navigation");
store.Import(new(1, [], [same], []), true, false);
Check(store.Nodes.Length == 1, "reimport of absorbed ledger ID stays idempotent");
var changed = first with { Link = first.Link.Replace("sni=site.example.org", "sni=updated.example.org"), InventoriedAt = DateTimeOffset.UtcNow };
store.Import(new(1, [], [changed], []), true, false);
Check(ShareLink.Parse(store.Nodes.Single().Link).Sni == "updated.example.org", "server refresh updates changed client route");
Check(store.Nodes.Single().Sources.Any(s => s.Link == same.Link), "source-specific original configuration survives merge");

var v2 = Path.Combine(root, "fixture-v2"); Directory.CreateDirectory(Path.Combine(v2, "guiConfigs"));
using (var db = new SqliteConnection("Data Source=" + V2Sync.Database(v2)))
{
    db.Open(); using var q = db.CreateCommand();
    q.CommandText = """
        CREATE TABLE SubItem(Id TEXT,Remarks TEXT);
        INSERT INTO SubItem VALUES('group','Group');
        CREATE TABLE ProfileItem(IndexId TEXT,Subid TEXT,Address TEXT,ConfigType INTEGER,Port INTEGER,Password TEXT,Remarks TEXT,Network TEXT,StreamSecurity TEXT,Sni TEXT,Security TEXT);
        """; q.ExecuteNonQuery();
    q.CommandText = "INSERT INTO ProfileItem VALUES($id,'group','192.0.2.10',5,443,$auth,$name,'raw','tls','site.example.org','none')";
    q.Parameters.AddWithValue("$id", "one"); q.Parameters.AddWithValue("$auth", id); q.Parameters.AddWithValue("$name", "First"); q.ExecuteNonQuery();
    q.Parameters["$id"].Value = "two"; q.Parameters["$name"].Value = "Second"; q.ExecuteNonQuery();
    q.Parameters["$id"].Value = "three"; q.Parameters["$auth"].Value = secondId; q.ExecuteNonQuery();
}
var sync = new Store(Path.Combine(root, "sync")); sync.Save(host); sync.Save(first);
V2Sync.Apply(sync, v2, ["group"], false); var count = sync.Nodes.Length;
V2Sync.Apply(sync, v2, ["group"], false);
Check(count == 2 && sync.Nodes.Length == count, "two same-client v2 sources merge; distinct client remains; repeated sync stable");
Check(sync.Nodes.Single(n => NodeIdentity.Client(n) == id).Sources.Count(s => s.Kind == "v2rayN") == 2, "both v2 source identities retained");
using (var db = new SqliteConnection("Data Source=" + V2Sync.Database(v2)))
{
    db.Open(); using var q = db.CreateCommand(); q.CommandText = "DELETE FROM ProfileItem WHERE IndexId IN ('one','two')"; q.ExecuteNonQuery();
}
V2Sync.Apply(sync, v2, ["group"], true);
Check(!sync.Nodes.Single(n => n.VpsVerified).SourceRemoved, "deleted v2 sources do not pause server-verified node");
var previewStore = new Store(Path.Combine(root, "preview")); previewStore.Save(host); previewStore.Save(first); previewStore.Save(same);
var document = new InventoryDocument(1, [], [first], []);
var preview = previewStore.Import(document, false, false);
Check(previewStore.Nodes.Length == 2, "preview rolls back automatic reconciliation");
var applied = previewStore.Import(document, true, false);
Check(preview.Added == applied.Added && preview.Updated == applied.Updated && preview.Merged == applied.Merged && applied.Merged == 1,
    "preview and applied counts include identical duplicate reconciliation");
var confirmation = new Store(Path.Combine(root, "confirmation")); confirmation.Save(host);
confirmation.Save(first with { Id = "server", Link = first.Link.Replace("sni=site.example.org", "sni=other.example.org") });
using (var db = new SqliteConnection("Data Source=" + V2Sync.Database(v2)))
{
    db.Open(); using var q = db.CreateCommand();
    q.CommandText = "INSERT INTO ProfileItem VALUES('one','group','192.0.2.10',5,443,$auth,'First','raw','tls','site.example.org','none')";
    q.Parameters.AddWithValue("$auth", id); q.ExecuteNonQuery();
}
V2Sync.Apply(confirmation, v2, ["group"], false);
var differentRoute = confirmation.Nodes.Single(n => n.V2Id == "one");
Check(confirmation.FindDuplicateCandidates().Any(p => p.Disposition == "Review"), "route conflict exposed for manual review");
confirmation.MergeNodes("server", differentRoute.Id);
var confirmedCount = confirmation.Nodes.Length;
V2Sync.Apply(confirmation, v2, ["group"], false);
Check(confirmation.Nodes.Length == confirmedCount && confirmation.Nodes.Single(n => n.Id == "server").Sources.Any(s => s.Key == "one" && s.Confirmed),
    "manual source association persists across synchronization");
try
{
    confirmation.MergeNodes("server", confirmation.Nodes.Single(n => NodeIdentity.Client(n) == secondId).Id);
    throw new Exception("different clients accepted");
}
catch (InvalidDataException) { Check(true, "manual confirmation cannot merge different clients"); }
var oldBackup = new Store(Path.Combine(root, "old-backup")); oldBackup.Save(host); oldBackup.Save(first); oldBackup.Save(same);
oldBackup.Record(new("node", first.Id, timestamp, "可用", Value: 100));
oldBackup.Record(new("node", same.Id, timestamp, "超时"));
oldBackup.Record(new("speed", same.Id, timestamp, "完成", Value: 20));
oldBackup.Record(new("task", "history", timestamp, "完成", Wire.Encode(new Inspection("nodes", "手动", timestamp, "完成", [new("node", same.Id, same.Name, "成功", "")]))));
oldBackup.Queue("node", same.Id);
var archive = Path.Combine(root, "duplicate-history.vpsbackup");
oldBackup.ExportBackup(archive, "fixture-backup-password");
var restored = new Store(Path.Combine(root, "merge-replay"));
restored.ImportBackup(archive, "fixture-backup-password", false);
var once = restored.CaptureBackup();
restored.ImportBackup(archive, "fixture-backup-password", false);
var twice = restored.CaptureBackup();
Check(once.Observations.Length == 4 && twice.Observations.Length == 4 &&
    restored.History("node", restored.Nodes.Single().Id, DateTimeOffset.MinValue).Select(o => o.State).Order().SequenceEqual(new[] { "可用", "超时" }.Order()),
    "replaying a backup with absorbed IDs deduplicates history while preserving timestamp conflicts");
Check(Wire.Decode<Inspection>(restored.History("task", "history", DateTimeOffset.MinValue).Single().Detail).Items.Single().Id == restored.Nodes.Single().Id &&
    restored.Peek()!.TargetId == restored.Nodes.Single().Id, "backup remaps task history and queued work before replay deduplication");
if (args.Contains("--real"))
{
    var real = new Store(Path.Combine(root, "real"));
    var alias = Environment.GetEnvironmentVariable("VPSNODE_TEST_SSH_ALIAS") ?? throw new ArgumentException("Set VPSNODE_TEST_SSH_ALIAS to an explicitly authorized host.");
    var realDirectory = Environment.GetEnvironmentVariable("VPSNODE_TEST_V2RAYN_DIRECTORY") ?? throw new ArgumentException("Set VPSNODE_TEST_V2RAYN_DIRECTORY to your local client directory.");
    var actualHost = await SshInventory.FromAlias(alias, default); real.Save(actualHost);
    var inventory = await SshInventory.Probe(real, actualHost, true, default);
    Check(inventory.State == "SSH 可登录" && real.Nodes.Length > 0, "authorized host inventory succeeded");
    var groups = V2Sync.Groups(realDirectory).Select(g => g.Id).ToArray();
    var summary = V2Sync.Apply(real, realDirectory, groups, false); var realCount = real.Nodes.Length;
    var repeat = V2Sync.Apply(real, realDirectory, groups, false);
    Check(real.Nodes.Length == realCount && repeat.Added == 0 && repeat.Updated == 0, "real v2rayN repeated sync has stable node count and no spurious updates");
    Console.WriteLine($"REAL nodes={realCount}; first added={summary.Added}, conflicts={summary.Conflicts.Length}; repeat added={repeat.Added}, updated={repeat.Updated}; host sources=" +
        string.Join(",", real.Nodes.Where(n => n.HostId == actualHost.Id).Select(n => $"port:{n.Port}/sources:{n.Sources.Count(s=>s.Kind=="v2rayN")}/client:{(NodeIdentity.Client(n).Length>0)}")));
}
Console.WriteLine($"{passed} identity checks passed.");
