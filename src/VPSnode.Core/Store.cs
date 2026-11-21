using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace VPSnode.Core;

public sealed partial class Store
{
    public string DirectoryPath { get; }

    static Store() => SQLitePCL.Batteries_V2.Init();

    public Store(string? path = null)
    {
        DirectoryPath = Path.GetFullPath(path ?? Environment.GetEnvironmentVariable("VPSNODE_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VPSnode"));
        Directory.CreateDirectory(DirectoryPath);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS assets(kind TEXT NOT NULL, id TEXT NOT NULL, data BLOB NOT NULL,
                PRIMARY KEY(kind,id));
            CREATE TABLE IF NOT EXISTS observations(kind TEXT NOT NULL, id TEXT NOT NULL, time TEXT NOT NULL, data BLOB NOT NULL,
                PRIMARY KEY(kind,id,time));
            CREATE INDEX IF NOT EXISTS observations_time ON observations(time);
            CREATE TABLE IF NOT EXISTS requests(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, target TEXT NOT NULL,
                UNIQUE(kind,target));
            CREATE TABLE IF NOT EXISTS notices(id TEXT PRIMARY KEY, time TEXT NOT NULL);
            """;
        command.ExecuteNonQuery();
    }

    // SQLite increments data_version on this dedicated read connection after another connection commits.
    public SqliteCommand WatchChanges()
    {
        var db = Open();
        var command = db.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return command;
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DirectoryPath, "vpsnode.db"),
            DefaultTimeout = 10
        }.ToString());
        db.Open();
        return db;
    }

    private static byte[] Protect<T>(T value)
    {
        var plain = Encoding.UTF8.GetBytes(Wire.Encode(value));
        try { return ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private static T Unprotect<T>(object value)
    {
        var plain = ProtectedData.Unprotect((byte[])value, null, DataProtectionScope.CurrentUser);
        try { return Wire.Decode<T>(Encoding.UTF8.GetString(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal SyncSummary ApplySync(Func<Node[], Host[], (Node[] Changed, SyncSummary Summary)> plan)
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        var (changed, summary) = plan(List<Node>(db, tx, "node"), List<Host>(db, tx, "host"));
        foreach (var node in changed)
        {
            Validation.Node(node);
            Put(db, tx, "node", node.Id, node);
        }
        ReconcileNodes(db, tx);
        tx.Commit();
        return summary;
    }

    private static string Stamp(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql,
        params (string Name, object Value)[] parameters)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return command;
    }

    public T[] List<T>(string kind)
    {
        using var db = Open();
        return List<T>(db, null, kind);
    }

    private static T[] List<T>(SqliteConnection db, SqliteTransaction? tx, string kind)
    {
        using var q = Command(db, tx, "SELECT data FROM assets WHERE kind=$k ORDER BY id", ("$k", kind));
        using var rows = q.ExecuteReader();
        List<T> values = [];
        while (rows.Read()) values.Add(Unprotect<T>(rows[0]));
        return [.. values];
    }

    public Settings Settings => List<Settings>("settings").FirstOrDefault() ?? new();
    public Host[] Hosts => List<Host>("host");
    public Node[] Nodes => List<Node>("node");
    public WebTarget[] Websites => List<WebTarget>("web");

    public void Save(Settings value) { Validation.Settings(value); Put("settings", "main", value); }
    public void Save(Host value) { Validation.Host(value); Put("host", value.Id, value); }
    public void Save(Node value) { Validation.Node(value); Put("node", value.Id, value); }
    public void Save(WebTarget value) { Validation.Web(value); Put("web", value.Id, WebProbe.ForStorage(value)); }

    private void Put<T>(string kind, string id, T value)
    {
        using var db = Open();
        Put(db, null, kind, id, value);
    }

    private static void Put<T>(SqliteConnection db, SqliteTransaction? tx, string kind, string id, T value)
    {
        using var q = Command(db, tx, """
            INSERT INTO assets(kind,id,data) VALUES($k,$i,$d)
            ON CONFLICT(kind,id) DO UPDATE SET data=excluded.data
            """, ("$k", kind), ("$i", id), ("$d", Protect(value)));
        q.ExecuteNonQuery();
    }

    public void Delete(string kind, string id)
    {
        if (kind is not ("host" or "node" or "web")) throw new InvalidDataException("对象类型无效。");
        using var db = Open();
        using var tx = db.BeginTransaction();
        if (kind == "host")
            foreach (var n in List<Node>(db, tx, "node").Where(n => n.HostId == id))
                Put(db, tx, "node", n.Id, n with { HostId = "" });
        foreach (var w in List<WebTarget>(db, tx, "web"))
        {
            if (kind == "web" && w.Id == id &&
                string.Equals(WebProbe.Address(w), WebProbe.Domain(w), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(WebProbe.Name(w), WebProbe.Domain(w), StringComparison.OrdinalIgnoreCase))
            {
                foreach (var n in List<Node>(db, tx, "node").Where(n => n.HostId == w.HostId && n.Link.Length > 0 && n.Security == "tls"))
                {
                    var link = ShareLink.Parse(n.Link);
                    if (!string.Equals(link.Sni, WebProbe.Domain(w), StringComparison.OrdinalIgnoreCase) || (n.Port == 0 ? 443 : n.Port) != w.Port) continue;
                    var key = WebProbe.PublicId(n.HostId, link.Sni, n.Port);
                    Put(db, tx, "hidden-web", key, key);
                }
            }
            if (kind == "host" && w.HostId == id) Put(db, tx, "web", w.Id, w with { HostId = "" });
            if (kind == "node" && (w.CertificateNodeIds.Contains(id) || w.FallbackNodeIds.Contains(id)))
                Put(db, tx, "web", w.Id, w with { CertificateNodeIds = w.CertificateNodeIds.Where(n => n != id).ToArray(),
                    FallbackNodeIds = w.FallbackNodeIds.Where(n => n != id).ToArray() });
        }
        if (kind == "web") Put(db, tx, "hidden-web", id, id);
        var kinds = kind switch {
            "host" => new[] { "host", "inventory", "host-nodes" },
            "node" => new[] { "node", "speed", "sni", "sni-node", "sni-vps", "sni-local" },
            _ => new[] { "web", "web80", "web443", "dns", "tls" }
        };
        foreach (var k in kinds)
        {
            using var cleanup = Command(db, tx, "DELETE FROM observations WHERE kind=$k AND id=$i; DELETE FROM requests WHERE kind=$k AND target=$i", ("$k", k), ("$i", id));
            cleanup.ExecuteNonQuery();
        }
        using var q = Command(db, tx, "DELETE FROM assets WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id));
        q.ExecuteNonQuery();
        foreach (var task in List<InspectionTask>(db, tx, "inspection-task").Where(t => !t.Terminal && t.TargetId == id && kinds.Contains(t.Kind)))
            Put(db, tx, "inspection-task", task.Id, task with { State = "canceled", CancelRequested = true,
                ErrorCode = "target_deleted", Error = "目标已删除", Updated = DateTimeOffset.UtcNow });
        tx.Commit();
    }

    public void Record(Observation value)
    {
        using var db = Open();
        using var q = Command(db, null, "INSERT OR REPLACE INTO observations VALUES($k,$i,$t,$d)",
            ("$k", value.Kind), ("$i", value.Id), ("$t", Stamp(value.At)), ("$d", Protect(value)));
        q.ExecuteNonQuery();
    }

    public Observation[] History(string kind, string id, DateTimeOffset since, int limit = 5000)
    {
        using var db = Open();
        using var q = Command(db, null, """
            SELECT data FROM observations WHERE kind=$k AND id=$i AND time >= $t
            ORDER BY time DESC LIMIT $l
            """, ("$k", kind), ("$i", id), ("$t", Stamp(since)), ("$l", limit));
        using var rows = q.ExecuteReader();
        List<Observation> values = [];
        while (rows.Read()) values.Add(Unprotect<Observation>(rows[0]));
        return [.. values];
    }

    public Dictionary<string, Observation> LatestDownloads()
    {
        using var db = Open();
        using var q = Command(db, null, "SELECT id,data FROM observations WHERE kind='speed' ORDER BY id,time DESC");
        using var reader = q.ExecuteReader();
        Dictionary<string, Observation> results = [];
        while (reader.Read())
        {
            var id = reader.GetString(0);
            if (results.ContainsKey(id)) continue;
            var observation = Unprotect<Observation>(reader[1]);
            if (observation.State == "完成" && observation.Value.HasValue) results.Add(id, observation);
        }
        return results;
    }

    public Dictionary<(string Kind, string Id), Observation> Latest()
    {
        using var db = Open();
        using var q = Command(db, null, """
            SELECT o.data FROM observations o JOIN
            (SELECT kind,id,MAX(time) AS newest FROM observations GROUP BY kind,id) n
            ON o.kind=n.kind AND o.id=n.id AND o.time=n.newest
            """);
        using var rows = q.ExecuteReader();
        Dictionary<(string, string), Observation> results = [];
        while (rows.Read())
        {
            var value = Unprotect<Observation>(rows[0]);
            results[(value.Kind, value.Id)] = value;
        }
        return results;
    }

    public void Trim(int days)
    {
        using var db = Open();
        using var q = Command(db, null, "DELETE FROM observations WHERE time < $t",
            ("$t", Stamp(DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365)))));
        q.ExecuteNonQuery();
        foreach (var task in Tasks.Where(t => t.Terminal && t.Updated < DateTimeOffset.UtcNow.AddDays(-Math.Clamp(days, 1, 365))))
        {
            using var remove = Command(db, null, "DELETE FROM assets WHERE kind='inspection-task' AND id=$i", ("$i", task.Id));
            remove.ExecuteNonQuery();
        }
    }

    public bool MarkNotice(string id)
    {
        using var db = Open();
        using var q = Command(db, null, "INSERT OR IGNORE INTO notices VALUES($i,$t)", ("$i", id),
            ("$t", Stamp(DateTimeOffset.UtcNow)));
        return q.ExecuteNonQuery() == 1;
    }

    public void Queue(string kind, string id)
    {
        using var db = Open();
        Queue(db, null, kind, id);
    }

    private static void Queue(SqliteConnection db, SqliteTransaction? tx, string kind, string id)
    {
        using var q = Command(db, tx, "INSERT OR IGNORE INTO requests(kind,target) VALUES($k,$i)",
            ("$k", kind), ("$i", id));
        q.ExecuteNonQuery();
    }

    public Request? Peek()
    {
        using var db = Open();
        using var q = Command(db, null, "SELECT id,kind,target FROM requests ORDER BY id LIMIT 1");
        using var r = q.ExecuteReader();
        return r.Read() ? new(r.GetInt64(0), r.GetString(1), r.GetString(2)) : null;
    }

    public void Finish(Request request)
    {
        using var db = Open();
        using var q = Command(db, null, "DELETE FROM requests WHERE id=$i", ("$i", request.Id));
        q.ExecuteNonQuery();
    }

    public InventorySummary Import(InventoryDocument document, bool apply, bool enqueue = true)
    {
        if (document.Version != 1) throw new InvalidDataException("仅支持版本 1。");
        var hosts = document.Hosts ?? [];
        var nodes = (document.Nodes ?? []).Select(node => ShareLink.Normalize(node)).ToArray();
        var websites = (document.Websites ?? []).Select(WebProbe.ForStorage).ToArray();
        foreach (var value in hosts) Validation.Host(value);
        foreach (var value in nodes) Validation.Node(value);
        foreach (var value in websites) Validation.Web(value);
        using var db = Open();
        using var tx = db.BeginTransaction();
        var existingHosts = List<Host>(db, tx, "host").ToDictionary(x => x.Id);
        var existingNodes = List<Node>(db, tx, "node").ToDictionary(x => x.Id);
        var existingWeb = List<WebTarget>(db, tx, "web").ToDictionary(x => x.Id);
        var before = existingHosts.Values.Select(x => (Key: "host/" + x.Id, Data: Wire.Encode(x)))
            .Concat(existingNodes.Values.Select(x => (Key: "node/" + x.Id, Data: Wire.Encode(x))))
            .Concat(existingWeb.Values.Select(x => (Key: "web/" + x.Id, Data: Wire.Encode(x))))
            .ToDictionary(x => x.Key, x => x.Data);
        var knownHosts = existingHosts.Keys.Concat(hosts.Select(x => x.Id)).ToHashSet();
        var nodeIndex = existingNodes.Values.Concat(nodes).GroupBy(x => x.Id)
            .ToDictionary(group => group.Key, group => group.Last());
        foreach (var web in websites)
            foreach (var nodeId in web.FallbackNodeIds.Concat(web.CertificateNodeIds))
                if (!nodeIndex.TryGetValue(nodeId, out var node) ||
                    (web.HostId.Length > 0 && node.HostId.Length > 0 && web.HostId != node.HostId))
                    throw new InvalidDataException("网站关联的节点不存在或属于其他 VPS。");
        if (nodes.Any(x => x.HostId.Length > 0 && !knownHosts.Contains(x.HostId)) ||
            websites.Any(x => x.HostId.Length > 0 && !knownHosts.Contains(x.HostId)))
            throw new InvalidDataException("节点或网站引用了不存在的 VPS ID。");
        int added = 0, updated = 0;
        List<string> conflicts = [];

        void Item<T>(string kind, string id, T value, Dictionary<string, T> existing, Func<T, T, T> merge)
        {
            if (existing.TryGetValue(id, out var old))
            {
                value = merge(old, value);
                if (Wire.Encode(old) == Wire.Encode(value)) return;
                updated++;
            }
            else added++;
            existing[id] = value;
            Put(db, tx, kind, id, value);
            if (enqueue) Queue(db, tx, kind, id);
        }

        foreach (var host in hosts)
        {
            Item("host", host.Id, host, existingHosts, (old, value) =>
            {
                if (!string.IsNullOrEmpty(old.Address) && !string.IsNullOrEmpty(value.Address) &&
                    !string.Equals(old.Address, value.Address, StringComparison.OrdinalIgnoreCase))
                {
                    conflicts.Add($"VPS {old.Id} 的地址与现有记录不同，请手工核对。");
                    return old;
                }
                return value with
                {
                    Address = Field(value.Address, old.Address), Ipv6 = Field(value.Ipv6, old.Ipv6),
                    SshAlias = Field(value.SshAlias, old.SshAlias), User = Field(value.User, old.User),
                    PasswordCipher = Field(value.PasswordCipher, old.PasswordCipher),
                    KeyPath = Field(value.KeyPath, old.KeyPath), Notes = Field(old.Notes, value.Notes),
                    PanelPath = Field(value.PanelPath, old.PanelPath), TunnelPort = value.TunnelPort ?? old.TunnelPort,
                    RemotePanelPort = value.InventoriedAt is null ? old.RemotePanelPort : value.RemotePanelPort,
                    RemotePanelPath = value.InventoriedAt is null ? old.RemotePanelPath : value.RemotePanelPath,
                    PanelScheme = value.InventoriedAt is null ? old.PanelScheme : value.PanelScheme,
                    Hostname = Field(value.Hostname, old.Hostname), Hardware = Field(value.Hardware, old.Hardware),
                    Os = Field(value.Os, old.Os), Provider = Field(value.Provider, old.Provider),
                    InventoriedAt = value.InventoriedAt ?? old.InventoriedAt,
                    Ports = value.Ports.Length == 0 && value.InventoriedAt is null ? old.Ports : value.Ports
                };
            });
        }
        Dictionary<string, string> mappedNodes = [];
        foreach (var incoming in nodes)
        {
            var node = incoming;
            var exact = existingNodes.GetValueOrDefault(node.Id) ?? existingNodes.Values.FirstOrDefault(n =>
                n.Sources.Any(s => s.Kind == "ledger" && s.Key == node.Id));
            if (exact is null)
            {
                var matches = existingNodes.Values.Where(n => NodeIdentity.Compare(n, node, existingHosts.Values.ToArray()).Disposition == "Same").ToArray();
                if (matches.Length == 1) exact = matches[0];
                else if (matches.Length > 1) conflicts.Add($"节点 {node.Id} 有多个可能关联，已保留待确认。");
            }
            if (exact is not null)
            {
                if ((NodeIdentity.Client(exact).Length > 0 && NodeIdentity.Client(node).Length > 0 &&
                     NodeIdentity.Client(exact) != NodeIdentity.Client(node)) ||
                    exact.HostId.Length > 0 && node.HostId.Length > 0 && exact.HostId != node.HostId)
                {
                    conflicts.Add($"节点 {node.Id} 的客户端身份或主机归属冲突，已保留原记录。");
                    continue;
                }
                mappedNodes[incoming.Id] = exact.Id;
                var newerServer = node.VpsVerified && node.InventoriedAt is not null &&
                    (exact.InventoriedAt is null || node.InventoriedAt >= exact.InventoriedAt);
                node = NodeIdentity.Merge(exact, node, preferIncoming: exact.Id == incoming.Id || newerServer);
            }
            else node = node with { Sources = NodeIdentity.Sources(node), ClientId = NodeIdentity.Client(node) };
            Item("node", node.Id, node, existingNodes, (_, value) => value);
        }
        foreach (var originalTarget in websites)
        {
            var target = originalTarget with
            {
                FallbackNodeIds = originalTarget.FallbackNodeIds.Select(id => mappedNodes.GetValueOrDefault(id, id)).Distinct().ToArray(),
                CertificateNodeIds = originalTarget.CertificateNodeIds.Select(id => mappedNodes.GetValueOrDefault(id, id)).Distinct().ToArray()
            };
            Item("web", target.Id, target, existingWeb, (old, value) => value with
            {
                HostId = Field(value.HostId, old.HostId), Url = Field(value.Url, old.Url),
                Domain = Field(value.Domain, old.Domain), ConnectAddress = Field(value.ConnectAddress, old.ConnectAddress),
                Sni = Field(value.Sni, old.Sni), Contains = Field(value.Contains, old.Contains),
                FallbackNodeIds = value.FallbackNodeIds.Length > 0 ? value.FallbackNodeIds : old.FallbackNodeIds,
                CertificateNodeIds = value.CertificateNodeIds.Length > 0 ? value.CertificateNodeIds : old.CertificateNodeIds,
                ExpectedStatus = value.ExpectedStatus ?? old.ExpectedStatus
            });
        }
        var merged = ReconcileNodes(db, tx);
        var after = List<Host>(db, tx, "host").Select(x => (Key: "host/" + x.Id, Data: Wire.Encode(x)))
            .Concat(List<Node>(db, tx, "node").Select(x => (Key: "node/" + x.Id, Data: Wire.Encode(x))))
            .Concat(List<WebTarget>(db, tx, "web").Select(x => (Key: "web/" + x.Id, Data: Wire.Encode(x)))).ToArray();
        added = after.Count(x => !before.ContainsKey(x.Key));
        updated = after.Count(x => before.TryGetValue(x.Key, out var previous) && previous != x.Data);
        if (apply) tx.Commit(); // Preview executes the same transaction, then rolls it back on disposal.
        return new(added, updated, [.. conflicts], merged);
    }

    internal static string Field(string primary, string alternate) => string.IsNullOrWhiteSpace(primary) ? alternate : primary;

    private static Dictionary<string, string> Merge(Dictionary<string, string> old, Dictionary<string, string> values)
    {
        Dictionary<string, string> output = new(old, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in values) output[key] = value;
        return output;
    }
}
