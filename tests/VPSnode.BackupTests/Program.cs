using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VPSnode.Core;

if (args.Length != 1 || Directory.Exists(args[0])) throw new Exception("Supply a fresh isolated directory.");
var root = Path.GetFullPath(args[0]);
var a = new Store(Path.Combine(root, "a"));
const string password = "fictional-backup-password";
const string secret = "fictional-ssh-secret";
var cipher = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser));
a.Save(new Host { Id = "h", Name = "fixture", Address = "192.0.2.1", PasswordCipher = cipher, KeyPath = "Z:/missing/key" });
a.Save(ShareLink.New("vless://00000000-0000-4000-8000-000000000001@192.0.2.1:443?security=tls&sni=site.example.org&type=tcp#Fixture", "h", "n"));
a.Save(new WebTarget { Id = "w", Name = "site", HostId = "h", Domain = "site.example.org", FallbackNodeIds = ["n"], CertificateNodeIds = ["n"] });
a.Save(new Settings { Theme = "dark", V2Directory = "Z:/missing/v2rayN", FollowGroups = ["group"], ScheduledInspectionEnabled = true });
for (var i = 0; i < 5001; i++) a.Record(new("node", "n", DateTimeOffset.UnixEpoch.AddSeconds(i), "可用", Value: 90));
a.Record(new("speed", "n", DateTimeOffset.UnixEpoch, "完成", Value: 20));
a.Record(new("task", "all", DateTimeOffset.UnixEpoch, "完成", "巡检记录"));
a.Queue("node", "n");
a.MarkNotice("certificate-warning");
var first = Path.Combine(root, "first.vpsbackup");
a.ExportBackup(first, password);
Check(!File.ReadAllText(first).Contains(secret) && !File.ReadAllText(first).Contains("site.example.org"), "no plaintext assets or secrets in backup");
var preview = a.PreviewBackup(first, password);
Check(preview is { Hosts: 1, Nodes: 1, Websites: 1, Observations: 5003, Requests: 1, Notices: 1 }, "preview includes full history beyond UI limit");
Check(preview.Warnings.Any(w => w.Contains("私钥")), "external key and dependency warning");
var b = new Store(Path.Combine(root, "b"));
b.ImportBackup(first, password, true);
Check(b.Settings.ScheduledInspectionEnabled, "explicit scheduler choice survives encrypted restore");
Check(Wire.Encode(a.CaptureBackup()) == Wire.Encode(b.CaptureBackup()), "complete semantic round trip including password and all tables");
var second = Path.Combine(root, "second.vpsbackup");
b.ExportBackup(second, password);
Check(File.ReadAllText(first) != File.ReadAllText(second), "fresh salt and nonce on each export");
var c = new Store(Path.Combine(root, "c"));
c.ImportBackup(second, password, true);
Check(Wire.Encode(a.CaptureBackup()) == Wire.Encode(c.CaptureBackup()), "export import export import consistency");
b.Save(b.Hosts.Single() with { Notes = "local wins" });
b.ImportBackup(first, password, false);
b.ImportBackup(first, password, false);
Check(b.Hosts.Single().Notes == "local wins" && b.CaptureBackup().Observations.Length == 5003 && b.CaptureBackup().Requests.Length == 1, "merge preserves local edits and is idempotent");
var before = Wire.Encode(b.CaptureBackup());
Reject(() => b.ImportBackup(first, "wrong-password", true), "wrong password rejected");
Check(before == Wire.Encode(b.CaptureBackup()), "wrong password leaves database unchanged");
var envelope = JsonNode.Parse(File.ReadAllText(first))!.AsObject();
envelope["version"] = 999;
var bad = Path.Combine(root, "bad.vpsbackup");
File.WriteAllText(bad, envelope.ToJsonString());
Reject(() => b.ImportBackup(bad, password, true), "future version rejected");
envelope["version"] = 1;
var bytes = Convert.FromBase64String(envelope["payload"]!.GetValue<string>());
bytes[10] ^= 1;
envelope["payload"] = Convert.ToBase64String(bytes);
File.WriteAllText(bad, envelope.ToJsonString());
Reject(() => b.ImportBackup(bad, password, true), "tampered encrypted payload rejected");
Check(before == Wire.Encode(b.CaptureBackup()), "invalid imports are atomic");
Reject(() => b.ExportBackup(bad, "short"), "weak password rejected");
b.Save(b.Hosts.Single() with { Address = "192.0.2.99" });
var conflictBefore = Wire.Encode(b.CaptureBackup());
Reject(() => b.ImportBackup(first, password, false), "same ID with different host identity rejects merge");
Check(conflictBefore == Wire.Encode(b.CaptureBackup()), "conflicting merge leaves every table unchanged");
var sourceBackup = new Store(Path.Combine(root, "source-backup"));
var sourceLocal = new Store(Path.Combine(root, "source-local"));
var sharedNode = ShareLink.New("vless://00000000-0000-4000-8000-000000000009@192.0.2.9:443?security=tls&sni=source.example.org&type=tcp#Sources", id: "shared-node");
sourceBackup.Save(sharedNode with { Notes = "backup notes", Sources = [
    new("v2rayN", "backup-only", "Z:/client", Link: sharedNode.Link),
    new("v2rayN", "shared", "Z:/client", Name: "backup name", Link: sharedNode.Link)] });
sourceLocal.Save(sharedNode with { Notes = "local notes", Sources = [
    new("v2rayN", "local-only", "Z:/client", Link: sharedNode.Link),
    new("v2rayN", "shared", "Z:/client", Name: "local name", Link: sharedNode.Link)] });
sourceBackup.Record(new("node", sharedNode.Id, DateTimeOffset.UnixEpoch, "可用", Value: 91));
sourceLocal.Record(new("speed", sharedNode.Id, DateTimeOffset.UnixEpoch, "完成", Value: 21));
sourceLocal.Record(new("node", sharedNode.Id, DateTimeOffset.UnixEpoch, "超时"));
var sourceFile = Path.Combine(root, "sources.vpsbackup");
sourceBackup.ExportBackup(sourceFile, password);
sourceLocal.ImportBackup(sourceFile, password, false);
var mergedSources = sourceLocal.Nodes.Single();
Check(mergedSources.Notes == "local notes" && mergedSources.Sources.Count(s => s.Kind == "v2rayN") == 3 &&
    mergedSources.Sources.Single(s => s.Key == "shared").Name == "local name", "same ID merge unions source identities and retains local fields and source precedence");
var sourceSnapshot = Wire.Encode(sourceLocal.CaptureBackup());
Check(sourceLocal.History("node", sharedNode.Id, DateTimeOffset.MinValue).Select(o => o.State).Order().SequenceEqual(new[] { "可用", "超时" }.Order()),
    "same ID merge preserves distinct observations that share a timestamp");
sourceLocal.ImportBackup(sourceFile, password, false);
Check(sourceSnapshot == Wire.Encode(sourceLocal.CaptureBackup()) && sourceLocal.CaptureBackup().Observations.Length == 3,
    "same ID source union and both histories remain stable on repeated encrypted import");
Console.WriteLine("Backup tests passed.");

static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
static void Reject(Action action, string label) { try { action(); } catch (InvalidDataException) { Console.WriteLine("PASS " + label); return; } throw new Exception(label); }
