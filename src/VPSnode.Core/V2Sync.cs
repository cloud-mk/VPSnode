using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VPSnode.Core;

public static class V2Sync
{
    private sealed record Profile(string Id, string Group, Node? Node);
    private sealed record Snapshot(V2Group[] Groups, Profile[] Profiles);

    public static string Database(string directory) => Path.Combine(directory, "guiConfigs", "guiNDB.db");

    public static V2Group[] Groups(string directory) => Read(directory).Groups;

    internal static string? ProbeLink(string directory, Host? host, Node inbound)
    {
        if (host is null || !inbound.VpsVerified || NodeIdentity.Client(inbound).Length == 0) return null;
        var links = Read(directory).Profiles.Select(p => p.Node).OfType<Node>()
            .Where(n => NodeIdentity.Compare(inbound, n, [host]).Disposition == "Same")
            .Select(n => ShareLink.Parse(n.Link))
            .Select(link => (link with { Name = "", Options = link.Options.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value) }).Format())
            .Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return links.Length == 1 ? links[0] : null;
    }
    private static Snapshot Read(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !File.Exists(Database(directory)))
            throw new FileNotFoundException("请选择包含 guiConfigs\\guiNDB.db 的 v2rayN 目录。");

        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Database(directory), Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3
        }.ToString());
        db.Open();
        using var tx = db.BeginTransaction(deferred: true);
        using var groupQuery = db.CreateCommand();
        groupQuery.Transaction = tx;
        groupQuery.CommandText = "SELECT Id, Remarks FROM SubItem";
        Dictionary<string, string> names = new() { [""] = "未分组" };
        using (var r = groupQuery.ExecuteReader())
            while (r.Read())
                if (!r.IsDBNull(0) && r.GetString(0).Length > 0) names[r.GetString(0)] = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1);
        using var q = db.CreateCommand();
        q.Transaction = tx;
        q.CommandText = "SELECT * FROM ProfileItem";
        List<Profile> profiles = [];
        using (var rows = q.ExecuteReader())
        {
            var columns = Enumerable.Range(0, rows.FieldCount).ToDictionary(rows.GetName, StringComparer.OrdinalIgnoreCase);
            if (!new[] { "IndexId", "Subid", "Address", "ConfigType", "Port" }.All(columns.ContainsKey))
                throw new InvalidDataException("v2rayN 数据结构不兼容，已保留监控数据。");
            while (rows.Read())
            {
                string Field(string key) => columns.TryGetValue(key, out var index) && !rows.IsDBNull(index)
                    ? Convert.ToString(rows[index], CultureInfo.InvariantCulture) ?? "" : "";
                var id = Field("IndexId");
                var group = Field("Subid");
                if (string.IsNullOrEmpty(id)) throw new InvalidDataException("v2rayN 条目标识缺失。");
                profiles.Add(new(id, group, Build(Field, id, group)));
            }
        }
        tx.Commit();
        foreach (var profile in profiles) names.TryAdd(profile.Group, profile.Group);
        return new(names.Select(x => new V2Group(x.Key, x.Value, profiles.Count(p => p.Group == x.Key))).ToArray(),
            [.. profiles]);
    }

    private static Node? Build(Func<string, string> f, string id, string group)
    {
        var protocol = f("ConfigType");
        var scheme = protocol == "5" ? "vless" : protocol == "7" ? "hysteria2" : "";
        if (scheme.Length == 0 || !int.TryParse(f("Port"), out var port)) return null;
        try
        {
            var transport = Object(f("TransportExtra"));
            var proto = Object(f("ProtoExtra"));
            var auth = Store.Field(f("Password"), f("Id"));
            Dictionary<string, string> options = new();
            void Add(string key, string value)
            {
                if (!string.IsNullOrEmpty(value)) options[key] = value;
            }

            Add("sni", f("Sni"));
            Add("alpn", f("Alpn"));
            Add("fp", f("Fingerprint"));
            Add("pinSHA256", f("CertSha"));
            if (f("AllowInsecure") is "true" or "1" or "True") Add("allowInsecure", "1");
            if (scheme == "vless")
            {
                var network = Store.Field(f("Network"), "tcp");
                Add("type", network);
                Add("security", f("StreamSecurity"));
                Add("encryption", Store.Field(Get(proto, "vlessEncryption"), Store.Field(f("Security"), "none")));
                Add("flow", Store.Field(Get(proto, "flow"), f("Flow")));
                Add("path", Store.Field(Get(transport, "path"), f("Path")));
                Add("host", Store.Field(Get(transport, "host"), f("RequestHost")));
                Add("mode", Store.Field(Get(transport, "xhttpMode"), network == "xhttp" ? f("HeaderType") : ""));
                Add("headerType", Store.Field(Get(transport, "rawHeaderType"), network is "raw" or "tcp" ? f("HeaderType") : ""));
                Add("extra", Store.Field(Get(transport, "xhttpExtra"), f("Extra")));
                Add("pbk", f("PublicKey"));
                Add("sid", f("ShortId"));
                Add("spx", f("SpiderX"));
                Add("pqv", f("Mldsa65Verify"));
                Add("finalmask", f("Finalmask"));
                Add("ech", f("EchConfigList"));
                Add("pcs", f("VerifyPeerCertByName"));
                Add("cert", f("Cert"));
                if (f("MuxEnabled") is "1" or "true" or "True") Add("mux", "1");
            }
            else
            {
                Add("mport", Store.Field(Get(proto, "ports"), f("Ports")));
                Add("hopInterval", Get(proto, "hopInterval"));
                Add("upmbps", Get(proto, "upMbps"));
                Add("downmbps", Get(proto, "downMbps"));
                Add("realm", Get(proto, "hy2RealmUrl"));
                var password = Get(proto, "salamanderPass");
                if (password.Length > 0) { Add("obfs", "salamander"); Add("obfs-password", password); }
            }
            var link = new ShareLink(scheme, f("Address").Trim('[', ']'), port, auth, f("Remarks"), options).Format();
            var node = ShareLink.New(link) with { Manual = false, V2Id = id, GroupId = group, Source = "v2rayN" };
            return node;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or UriFormatException) { return null; }
    }

    private static JsonElement Object(string raw) => raw.Length == 0 ? default : JsonDocument.Parse(raw).RootElement.Clone();

    private static string Get(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        return element.EnumerateObject().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
            .Value.ToString();
    }

    public static SyncSummary Apply(Store store, string directory, IReadOnlyCollection<string> groups, bool auto)
    {
        var snapshot = Read(directory);
        var path = Path.GetFullPath(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return store.ApplySync((nodes, hosts) => Plan(snapshot, nodes, hosts, path, groups, auto));
    }

    private static (Node[] Changed, SyncSummary Summary) Plan(Snapshot snapshot, Node[] nodes, Host[] hosts,
        string path, IReadOnlyCollection<string> groups, bool auto)
    {
        var saved = nodes.ToDictionary(n => n.Id);
        var changed = new Dictionary<string, Node>();
        void Change(Node node) { changed[node.Id] = node; saved[node.Id] = node; }
        bool Source(NodeSource source, string id) => source.Kind == "v2rayN" && source.Key == id && NodeIdentity.SameDirectory(source.Directory, path);
        int added = 0, updated = 0, paused = 0, unsupported = 0;
        List<string> conflicts = [];
        foreach (var group in groups)
            if (!snapshot.Groups.Any(x => x.Id == group)) conflicts.Add("来源分组不可用，已保留该组巡检状态。");
        foreach (var profile in snapshot.Profiles.Where(x => groups.Contains(x.Group)))
        {
            if (profile.Node is not Node candidate) { unsupported++; continue; }
            var hostMatches = hosts.Where(h => NodeIdentity.Address(h.Address) == NodeIdentity.Address(candidate.Address) ||
                h.Ipv6.Length > 0 && NodeIdentity.Address(h.Ipv6) == NodeIdentity.Address(candidate.Address)).ToArray();
            candidate = candidate with
            {
                Id = "v2-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(path.ToUpperInvariant() + "|" + profile.Id)))[..24],
                V2Directory = path, HostId = hostMatches.Length == 1 ? hostMatches[0].Id : "",
                ClientId = NodeIdentity.Client(candidate)
            };
            var incomingSource = new NodeSource("v2rayN", profile.Id, path, profile.Group, candidate.Name, candidate.Link);
            candidate = candidate with { Sources = NodeIdentity.OrderedSources(NodeIdentity.Sources(candidate).Append(incomingSource)) };
            var existing = saved.Values.FirstOrDefault(n => NodeIdentity.Sources(n).Any(s => Source(s, profile.Id)));
            var confirmed = existing is not null && NodeIdentity.Sources(existing).Any(s => Source(s, profile.Id) && s.Confirmed);
            if (confirmed) incomingSource = incomingSource with { Confirmed = true };
            if (existing is not null && (existing.VpsVerified || existing.Manual))
            {
                var comparison = NodeIdentity.Compare(existing, candidate, hosts);
                if (comparison.Disposition != "Same" && !(confirmed && comparison.CanMerge))
                {
                    // Keep the server-verified client and its history; a changed source becomes a separate candidate.
                    var sources = NodeIdentity.Sources(existing).Select(s => Source(s, profile.Id)
                        ? s with { Kind = "v2rayN-history", Removed = true } : s).ToArray();
                    Change(existing with { Sources = sources, V2Id = "", V2Directory = "", GroupId = "" });
                    conflicts.Add($"节点 {existing.Id} 的来源配置已变化，保留原记录并单列新配置供核对。");
                    existing = null;
                }
            }
            if (existing is null)
            {
                var matches = saved.Values.Where(n => NodeIdentity.Compare(n, candidate, hosts).Disposition == "Same").ToArray();
                if (matches.Length == 1) existing = matches[0];
                else if (matches.Length > 1) conflicts.Add("存在多个兼容记录；保留来源记录，可在重复核对中选择。");
            }
            if (existing is null)
            {
                // An old ledger ID may remain after a source changed identity; do not overwrite it.
                if (saved.ContainsKey(candidate.Id)) candidate = candidate with { Id = candidate.Id + "-" +
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate.Link)))[..8] };
                Change(candidate); added++; continue;
            }
            var merged = NodeIdentity.Merge(existing, candidate, preferIncoming: !existing.Manual && !existing.VpsVerified);
            merged = merged with
            {
                Sources = NodeIdentity.OrderedSources(NodeIdentity.Sources(merged).Where(s => !Source(s, profile.Id)).Append(incomingSource)),
                SourceRemoved = false, ClientId = NodeIdentity.Client(merged)
            };
            if (Wire.Encode(merged) != Wire.Encode(existing)) { Change(merged); updated++; }
        }
        if (auto)
        {
            var present = snapshot.Profiles.Select(x => x.Id).ToHashSet();
            foreach (var node in saved.Values.ToArray())
            {
                var sources = NodeIdentity.Sources(node);
                var revised = sources.Select(s => s.Kind == "v2rayN" && NodeIdentity.SameDirectory(s.Directory, path) &&
                    groups.Contains(s.GroupId) && snapshot.Groups.Any(g => g.Id == s.GroupId) ? s with { Removed = !present.Contains(s.Key) } : s).ToArray();
                var removed = revised.Any(s => s.Kind == "v2rayN") && revised.Where(s => s.Kind == "v2rayN").All(s => s.Removed);
                var sourceRemoved = removed && !node.Manual && !node.VpsVerified;
                if (Wire.Encode(sources) != Wire.Encode(revised) || node.SourceRemoved != sourceRemoved)
                {
                    Change(node with { Sources = revised, SourceRemoved = sourceRemoved });
                    if (sourceRemoved && !node.SourceRemoved) paused++;
                }
            }
        }
        return ([.. changed.Values], new(added, updated, paused, unsupported, [.. conflicts]));
    }
}
