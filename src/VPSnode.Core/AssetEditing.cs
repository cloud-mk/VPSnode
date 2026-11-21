using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace VPSnode.Core;

public sealed partial class Store
{
    public (Host Host, InventorySummary Summary) ImportSshAlias(Host candidate)
    {
        var hosts = Hosts;
        var old = hosts.FirstOrDefault(h => h.Id == candidate.Id || h.SshAlias == candidate.SshAlias);
        if (old is null)
        {
            var direct = hosts.Where(h => h.SshAlias.Length == 0 && h.SshPort == candidate.SshPort &&
                h.Address.Equals(candidate.Address, StringComparison.OrdinalIgnoreCase) && h.User == candidate.User).ToArray();
            if (direct.Length == 1) old = direct[0];
        }
        if (old != null) candidate = candidate with { Id = old.Id, Name = old.Name, Enabled = old.Enabled };
        return (candidate, Import(new(1, [candidate], [], []), true, enqueue: false));
    }

    public static string Revision<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Wire.Encode(value))));

    // Patch explicit fields, including false, empty strings and arrays, in one transaction.
    // Inventory import retains its existing provenance/merge semantics.
    public object EditAsset(string kind, string id, JsonElement patch, bool create = false, string? revision = null)
    {
        if (!Validation.Token(id)) throw new InvalidDataException("资产 ID 无效。");
        using var db = Open(); using var tx = db.BeginTransaction();
        object result = kind switch
        {
            "host" => Edit(new Host { Id = id, Name = "" }),
            "node" => Edit(new Node { Id = id, Name = "" }),
            "web" => Edit(new WebTarget { Id = id, Name = "" }),
            _ => throw new InvalidDataException("资产类型应为 host、node 或 web。")
        };
        tx.Commit(); return result;

        T Edit<T>(T initial) where T : class
        {
            var old = List<T>(db, tx, kind).FirstOrDefault(x => JsonSerializer.SerializeToElement(x, Wire.Json).GetProperty("id").GetString() == id);
            if (!create && old == null && kind == "web") old = WebProbe.Targets(this).FirstOrDefault(w => w.Id == id) as T;
            if (create && old != null) throw new InvalidOperationException("资产 ID 已存在。");
            if (!create && old == null) throw new KeyNotFoundException("未找到资产。");
            if (revision != null && old != null && Revision(old) != revision) throw new InvalidOperationException("资产已被其他操作修改，请重新读取后再保存。");
            var value = Patch(old ?? initial, patch, kind == "host");
            if (value is Node node) value = (T)(object)ShareLink.Normalize(node);
            if (value is WebTarget web) value = (T)(object)WebProbe.ForStorage(web with {
                AutoFallbackNodeIds = old is WebTarget previous ? previous.AutoFallbackNodeIds : [] });
            if (JsonSerializer.SerializeToElement(value, Wire.Json).GetProperty("id").GetString() != id) throw new InvalidDataException("不可更改资产 ID。");
            ValidateAsset(db, tx, value);
            Put(db, tx, kind, id, value);
            return value;
        }
    }

    public Settings EditSettings(JsonElement patch)
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        var old = List<Settings>(db, tx, "settings").FirstOrDefault() ?? new();
        var value = Patch(old, patch);
        if (!value.V2Directory.Equals(old.V2Directory, StringComparison.OrdinalIgnoreCase))
            value = value with { FollowGroups = patch.TryGetProperty("followGroups", out _) ? value.FollowGroups : [] };
        Validation.Settings(value);
        Put(db, tx, "settings", "main", value); tx.Commit(); return value;
    }

    private static T Patch<T>(T original, JsonElement patch, bool hostPassword = false)
    {
        if (patch.ValueKind != JsonValueKind.Object) throw new InvalidDataException("更新内容应为 JSON 对象。");
        var data = JsonSerializer.SerializeToNode(original, Wire.Json)!.AsObject();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (var item in patch.EnumerateObject())
        {
            if (!seen.Add(item.Name)) throw new InvalidDataException("JSON 包含重复字段。");
            if (hostPassword && item.Name == "sshPassword")
            {
                if (item.Value.ValueKind != JsonValueKind.String) throw new InvalidDataException("sshPassword 应为字符串；空字符串清除密码。");
                var password = item.Value.GetString()!;
                var bytes = Encoding.UTF8.GetBytes(password);
                try { data["passwordCipher"] = password.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                continue;
            }
            var name = data.Select(p => p.Key).FirstOrDefault(k => k.Equals(item.Name, StringComparison.OrdinalIgnoreCase));
            if (name == null || name == "passwordCipher") throw new InvalidDataException("包含未知或不可写字段。");
            if (item.Value.ValueKind == JsonValueKind.Null && data[name] != null) throw new InvalidDataException("非空字段不可设为 null；清空文本使用空字符串。");
            data[name] = JsonNode.Parse(item.Value.GetRawText());
        }
        return data.Deserialize<T>(Wire.Json) ?? throw new InvalidDataException("更新内容为空。");
    }

    private static void ValidateAsset<T>(SqliteConnection db, SqliteTransaction tx, T value)
    {
        var hosts = List<Host>(db, tx, "host");
        switch (value)
        {
            case Host host: Validation.Host(host); break;
            case Node node:
                Validation.Node(node);
                if (node.HostId.Length > 0 && !hosts.Any(h => h.Id == node.HostId)) throw new InvalidDataException("节点关联 VPS 不存在。");
                if (node.Link.Length > 0 && ShareLink.Normalize(node) != node) throw new InvalidDataException("节点协议、地址和传输必须与分享链接一致。");
                break;
            case WebTarget web:
                Validation.Web(web);
                if (web.HostId.Length > 0 && !hosts.Any(h => h.Id == web.HostId)) throw new InvalidDataException("网站关联 VPS 不存在。");
                var nodes = List<Node>(db, tx, "node");
                if (web.CertificateNodeIds.Concat(web.FallbackNodeIds).Any(id => !nodes.Any(n => n.Id == id &&
                    (web.HostId.Length == 0 || n.HostId.Length == 0 || n.HostId == web.HostId)))) throw new InvalidDataException("网站关联节点不存在或属于其他 VPS。");
                break;
        }
    }

    private void SaveCompared<T>(string kind, string id, T value, T? expected = default) where T : class
    {
        using var db = Open(); using var tx = db.BeginTransaction();
        if (expected != null)
        {
            using var query = Command(db, tx, "SELECT data FROM assets WHERE kind=$k AND id=$i", ("$k", kind), ("$i", id));
            if (query.ExecuteScalar() is not byte[] bytes || Revision(Unprotect<T>(bytes)) != Revision(expected))
                throw new InvalidDataException("记录已被其他操作修改或删除，请重新打开后编辑。");
        }
        ValidateAsset(db, tx, value); Put(db, tx, kind, id, value); tx.Commit();
    }

    public void Save(Host value, Host expected) => SaveCompared("host", value.Id, value, expected);
    public void Save(Node value, Node expected) => SaveCompared("node", value.Id, value, expected);
    public void Save(WebTarget value, WebTarget expected) => SaveCompared("web", value.Id, WebProbe.ForStorage(value), WebProbe.ForStorage(expected));
}
