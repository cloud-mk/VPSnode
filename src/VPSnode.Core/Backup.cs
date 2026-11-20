using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPSnode.Core;

public sealed record BackupPreview(int Hosts, int Nodes, int Websites, int Observations, int Requests, int Notices, string[] Warnings);
public sealed record BackupAsset(string Kind, string Id, JsonElement Data);
public sealed record BackupNotice(string Id, string Time);
public sealed record BackupSnapshot(int Version, BackupAsset[] Assets, Observation[] Observations,
    Request[] Requests, BackupNotice[] Notices, Dictionary<string, string> SshPasswords);

// The entire payload, including credentials and metadata, is authenticated and encrypted.
// External private-key files are deliberately not read or embedded.
public sealed partial class Store
{
    private const int BackupIterations = 600_000;
    private const int MaximumBackupBytes = 256 * 1024 * 1024;
    private static readonly byte[] BackupAad = Encoding.UTF8.GetBytes("VPSnode.Backup/1/AES-256-GCM/PBKDF2-SHA256/600000");
    private sealed record BackupEnvelope(string Format, int Version, string Cipher, string Kdf, int Iterations,
        string Salt, string Nonce, string Tag, string Payload);

    public void ExportBackup(string path, string password)
    {
        if (password.Length < 12) throw new InvalidDataException("备份口令至少需要 12 个字符。请妥善保管，遗失无法恢复。");
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(CaptureBackup(), Wire.Json);
        byte[] salt = RandomNumberGenerator.GetBytes(16), nonce = RandomNumberGenerator.GetBytes(12), tag = new byte[16];
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, BackupIterations, HashAlgorithmName.SHA256, 32);
        try
        {
            if (plain.Length > MaximumBackupBytes / 2) throw new InvalidDataException("备份数据超过大小限制。");
            byte[] cipher = new byte[plain.Length];
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag, BackupAad);
            var envelope = new BackupEnvelope("VPSnode.Backup", 1, "AES-256-GCM", "PBKDF2-SHA256", BackupIterations,
                Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
            var fullPath = Path.GetFullPath(path);
            var temp = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, Wire.Encode(envelope), new UTF8Encoding(false));
                File.Move(temp, fullPath, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        finally { CryptographicOperations.ZeroMemory(plain); CryptographicOperations.ZeroMemory(key); }
    }

    public BackupSnapshot CaptureBackup()
    {
        using var db = Open();
        using var tx = db.BeginTransaction();
        List<BackupAsset> assets = [];
        Dictionary<string, string> passwords = [];
        using (var command = Command(db, tx, "SELECT kind,id,data FROM assets ORDER BY kind,id"))
        using (var rows = command.ExecuteReader())
            while (rows.Read())
            {
                var kind = rows.GetString(0);
                var data = Unprotect<JsonElement>(rows[2]);
                if (kind == "host")
                {
                    var host = data.Deserialize<Host>(Wire.Json)!;
                    if (host.PasswordCipher.Length > 0)
                    {
                        byte[] decoded = ProtectedData.Unprotect(Convert.FromBase64String(host.PasswordCipher), null, DataProtectionScope.CurrentUser);
                        try { passwords[host.Id] = Convert.ToBase64String(decoded); }
                        finally { CryptographicOperations.ZeroMemory(decoded); }
                        data = JsonSerializer.SerializeToElement(host with { PasswordCipher = "" }, Wire.Json);
                    }
                }
                assets.Add(new(kind, rows.GetString(1), data));
            }
        List<Observation> observations = [];
        using (var command = Command(db, tx, "SELECT data FROM observations ORDER BY kind,id,time"))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) observations.Add(Unprotect<Observation>(rows[0]));
        List<Request> requests = [];
        using (var command = Command(db, tx, "SELECT id,kind,target FROM requests ORDER BY id"))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) requests.Add(new(rows.GetInt64(0), rows.GetString(1), rows.GetString(2)));
        List<BackupNotice> notices = [];
        using (var command = Command(db, tx, "SELECT id,time FROM notices ORDER BY id"))
        using (var rows = command.ExecuteReader())
            while (rows.Read()) notices.Add(new(rows.GetString(0), rows.GetString(1)));
        return new(1, [.. assets], [.. observations], [.. requests], [.. notices], passwords);
    }

    private static BackupSnapshot ReadBackup(string path, string password)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Length > MaximumBackupBytes) throw new InvalidDataException("备份文件过大。");
            var envelope = Wire.Decode<BackupEnvelope>(File.ReadAllText(path));
            if (envelope.Format != "VPSnode.Backup" || envelope.Version != 1 || envelope.Cipher != "AES-256-GCM" ||
                envelope.Kdf != "PBKDF2-SHA256" || envelope.Iterations != BackupIterations)
                throw new InvalidDataException("备份格式或版本不受支持。");
            byte[] salt = Convert.FromBase64String(envelope.Salt), nonce = Convert.FromBase64String(envelope.Nonce),
                tag = Convert.FromBase64String(envelope.Tag), cipher = Convert.FromBase64String(envelope.Payload);
            if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16) throw new InvalidDataException("备份加密参数无效。");
            byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, BackupIterations, HashAlgorithmName.SHA256, 32), plain = new byte[cipher.Length];
            try
            {
                using (var aes = new AesGcm(key, 16)) aes.Decrypt(nonce, cipher, tag, plain, BackupAad);
                var snapshot = JsonSerializer.Deserialize<BackupSnapshot>(plain, Wire.Json) ?? throw new InvalidDataException("备份为空。");
                ValidateBackup(snapshot);
                return snapshot;
            }
            finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or NullReferenceException)
        { throw new InvalidDataException("备份内容或格式无效。", ex); }
        catch (CryptographicException ex) { throw new InvalidDataException("备份口令错误或文件已损坏。", ex); }
    }

    private static void ValidateBackup(BackupSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Assets is null || snapshot.Observations is null || snapshot.Requests is null ||
            snapshot.Notices is null || snapshot.SshPasswords is null) throw new InvalidDataException("备份数据版本或结构无效。");
        var assets = snapshot.Assets;
        if (assets.Any(a => !Validation.Token(a.Id) || !Validation.Token(a.Kind)) ||
            assets.Select(a => (a.Kind, a.Id)).Distinct().Count() != assets.Length)
            throw new InvalidDataException("备份包含无效或重复资产 ID。");
        var hosts = assets.Where(a => a.Kind == "host").Select(a => a.Data.Deserialize<Host>(Wire.Json)!).ToDictionary(h => h.Id);
        var nodes = assets.Where(a => a.Kind == "node").Select(a => a.Data.Deserialize<Node>(Wire.Json)!).ToDictionary(n => n.Id);
        foreach (var asset in assets)
        {
            string id = asset.Id;
            switch (asset.Kind)
            {
                case "host":
                    var host = asset.Data.Deserialize<Host>(Wire.Json)!;
                    Validation.Host(host);
                    if (host.Id != id || host.PasswordCipher.Length > 0) throw new InvalidDataException("备份主机凭据格式无效。");
                    break;
                case "node":
                    var node = asset.Data.Deserialize<Node>(Wire.Json)!;
                    Validation.Node(node);
                    if (node.Id != id || node.HostId.Length > 0 && !hosts.ContainsKey(node.HostId)) throw new InvalidDataException("备份节点关联无效。");
                    if (node.Link.Length > 0) _ = ShareLink.Parse(node.Link);
                    break;
                case "web":
                    var web = asset.Data.Deserialize<WebTarget>(Wire.Json)!;
                    Validation.Web(web);
                    if (web.Id != id || web.HostId.Length > 0 && !hosts.ContainsKey(web.HostId) ||
                        web.FallbackNodeIds.Concat(web.CertificateNodeIds).Any(n => !nodes.TryGetValue(n, out var linked) ||
                            web.HostId.Length > 0 && linked.HostId.Length > 0 && web.HostId != linked.HostId))
                        throw new InvalidDataException("备份网站关联无效。");
                    break;
                case "settings":
                    _ = asset.Data.Deserialize<Settings>(Wire.Json) ?? throw new InvalidDataException("备份设置无效。");
                    break;
            }
        }
        if (snapshot.Observations.Any(o => string.IsNullOrWhiteSpace(o.Kind) || o.Id is null || o.State is null) ||
            snapshot.Observations.Select(o => (o.Kind, o.Id, Stamp(o.At))).Distinct().Count() != snapshot.Observations.Length ||
            snapshot.Requests.Any(r => r.Id < 1 || r.Kind is null || r.TargetId is null) ||
            snapshot.Requests.Select(r => r.Id).Distinct().Count() != snapshot.Requests.Length ||
            snapshot.Requests.Select(r => (r.Kind, r.TargetId)).Distinct().Count() != snapshot.Requests.Length ||
            snapshot.Notices.Any(n => n.Id is null || !DateTimeOffset.TryParse(n.Time, out _)) ||
            snapshot.Notices.Select(n => n.Id).Distinct().Count() != snapshot.Notices.Length)
            throw new InvalidDataException("备份历史或任务结构无效。");
        foreach (var (id, value) in snapshot.SshPasswords)
        {
            if (!hosts.ContainsKey(id)) throw new InvalidDataException("备份密码引用了不存在的主机。");
            var bytes = Convert.FromBase64String(value);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static BackupPreview Preview(BackupSnapshot snapshot) => new(
        snapshot.Assets.Count(a => a.Kind == "host"), snapshot.Assets.Count(a => a.Kind == "node"),
        snapshot.Assets.Count(a => a.Kind == "web"), snapshot.Observations.Length, snapshot.Requests.Length, snapshot.Notices.Length,
        ["备份包含客户端与服务端配置、SSH 密码；恢复后由当前 Windows 用户重新加密。",
         "外部 SSH 私钥、OpenSSH 配置与 v2rayN 程序未包含；跨电脑恢复后需重新定位路径、别名与依赖。",
         "合并：相同 ID 保留本机内容，身份冲突时拒绝合并；历史去重，高可信重复节点自动关联。替换：原台账、历史与设置全部替换。",
         "待执行任务会恢复；桌面导入完成后继续自动巡检，请在导入前核对目标及依赖。"]);

    public BackupPreview PreviewBackup(string path, string password)
    {
        var snapshot = ReadBackup(path, password);
        using var db = Open();
        using var query = Command(db, null, "SELECT kind,id FROM assets");
        using var rows = query.ExecuteReader();
        HashSet<(string Kind, string Id)> current = [];
        while (rows.Read()) current.Add((rows.GetString(0), rows.GetString(1)));
        var shared = snapshot.Assets.Count(a => current.Contains((a.Kind, a.Id)));
        var preview = Preview(snapshot);
        return preview with { Warnings = [.. preview.Warnings,
            $"当前台账已有 {shared} 个相同 ID，合并保留本机内容并补充节点来源；其余 {snapshot.Assets.Length - shared} 个为新增候选（含设置），高可信重复节点会自动关联。"] };
    }

    public BackupPreview ImportBackup(string path, string password, bool replace)
    {
        var snapshot = ReadBackup(path, password);
        // Validate and re-protect every credential before beginning any mutation.
        var assets = snapshot.Assets.Select(asset =>
        {
            if (asset.Kind != "host" || !snapshot.SshPasswords.TryGetValue(asset.Id, out var password64)) return asset;
            var bytes = Convert.FromBase64String(password64);
            try
            {
                var host = asset.Data.Deserialize<Host>(Wire.Json)! with
                { PasswordCipher = Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)) };
                return asset with { Data = JsonSerializer.SerializeToElement(host, Wire.Json) };
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }).ToArray();
        using var db = Open();
        using var tx = db.BeginTransaction();
        var localNodes = replace ? new Dictionary<string, Node>() : List<Node>(db, tx, "node").ToDictionary(n => n.Id);
        if (!replace)
        {
            var localHosts = List<Host>(db, tx, "host").ToDictionary(h => h.Id);
            var localWeb = List<WebTarget>(db, tx, "web").ToDictionary(w => w.Id);
            foreach (var asset in assets)
            {
                bool conflict = false;
                if (asset.Kind == "host" && localHosts.TryGetValue(asset.Id, out var host))
                {
                    var incoming = asset.Data.Deserialize<Host>(Wire.Json)!;
                    conflict = host.Address.Length > 0 && incoming.Address.Length > 0 &&
                        NodeIdentity.Address(host.Address) != NodeIdentity.Address(incoming.Address);
                }
                if (asset.Kind == "node" && localNodes.TryGetValue(asset.Id, out var node))
                {
                    var incoming = asset.Data.Deserialize<Node>(Wire.Json)!;
                    conflict = NodeIdentity.Compare(node, incoming, [.. localHosts.Values]).Differences.Any(d => d.Conflict);
                }
                if (asset.Kind == "web" && localWeb.TryGetValue(asset.Id, out var web))
                {
                    var incoming = asset.Data.Deserialize<WebTarget>(Wire.Json)!;
                    conflict = web.Port != incoming.Port || NodeIdentity.Address(WebProbe.Address(web)) != NodeIdentity.Address(WebProbe.Address(incoming)) ||
                        NodeIdentity.Address(WebProbe.Name(web)) != NodeIdentity.Address(WebProbe.Name(incoming)) ||
                        web.HostId.Length > 0 && incoming.HostId.Length > 0 && web.HostId != incoming.HostId;
                }
                if (conflict) throw new InvalidDataException($"相同 ID {asset.Id} 的身份或路由冲突，已取消合并。请核对台账或使用完整替换。");
            }
        }
        if (replace)
        {
            using var clear = Command(db, tx, "DELETE FROM assets; DELETE FROM observations; DELETE FROM requests; DELETE FROM notices;");
            clear.ExecuteNonQuery();
        }
        foreach (var asset in assets)
        {
            if (!replace && asset.Kind == "node")
            {
                var local = localNodes.GetValueOrDefault(asset.Id);
                if (local is not null)
                {
                    var incoming = asset.Data.Deserialize<Node>(Wire.Json)!;
                    var sources = NodeIdentity.Sources(incoming).Concat(NodeIdentity.Sources(local))
                        .GroupBy(s => (s.Kind, s.Key, Directory: s.Directory.ToUpperInvariant())).Select(g => g.Last()).ToArray();
                    Put(db, tx, "node", local.Id, local with { Sources = sources });
                    continue;
                }
            }
            using var insert = Command(db, tx, "INSERT OR IGNORE INTO assets(kind,id,data) VALUES($k,$i,$d)",
                ("$k", asset.Kind), ("$i", asset.Id), ("$d", Protect(asset.Data)));
            insert.ExecuteNonQuery();
        }
        // Resolve absorbed node IDs before importing history, so a replay compares canonical records.
        Dictionary<string, string> aliases = [];
        if (!replace)
        {
            ReconcileNodes(db, tx);
            var nodes = List<Node>(db, tx, "node");
            var currentIds = nodes.Select(n => n.Id).ToHashSet();
            aliases = nodes.SelectMany(n => n.Sources.Where(s => s.Kind == "ledger" && !currentIds.Contains(s.Key)).Select(s => (s.Key, n.Id)))
                .GroupBy(p => p.Key).Where(g => g.Select(p => p.Id).Distinct().Count() == 1).ToDictionary(g => g.Key, g => g.First().Id);
        }
        string Remap(string kind, string id) => kind is "node" or "speed" or "sni" or "sni-node" or "sni-vps" or "sni-local"
            ? aliases.GetValueOrDefault(id, id) : id;
        foreach (var observation in snapshot.Observations)
        {
            var value = observation with { Id = Remap(observation.Kind, observation.Id) };
            if (value.Kind == "task" && aliases.Count > 0)
                try
                {
                    var task = Wire.Decode<Inspection>(value.Detail);
                    if (task?.Items is not null)
                        value = value with { Detail = Wire.Encode(task with { Items = task.Items.Select(i => i with { Id = Remap(i.Kind, i.Id) }).ToArray() }) };
                }
                catch (JsonException) { /* Preserve legacy task formats verbatim. */ }
            MergeObservation(db, tx, value);
        }
        foreach (var request in snapshot.Requests)
        {
            if (!replace) { Queue(db, tx, request.Kind, Remap(request.Kind, request.TargetId)); continue; }
            using var insert = Command(db, tx, "INSERT INTO requests(id,kind,target) VALUES($i,$k,$t)",
                ("$i", request.Id), ("$k", request.Kind), ("$t", request.TargetId));
            insert.ExecuteNonQuery();
        }
        foreach (var notice in snapshot.Notices)
        {
            using var insert = Command(db, tx, "INSERT OR IGNORE INTO notices(id,time) VALUES($i,$t)", ("$i", notice.Id), ("$t", notice.Time));
            insert.ExecuteNonQuery();
        }
        tx.Commit();
        return Preview(snapshot);
    }
}
