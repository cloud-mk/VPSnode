using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPSnode.Core;

public sealed record IdentityDifference(string Field, string Left, string Right, bool Conflict);
public sealed record NodeMatch(string LeftId, string RightId, string Disposition, string Reason,
    IdentityDifference[] Differences)
{
    public bool CanMerge => Disposition is "Same" or "Review";
    public bool CanAssociate => CanMerge && !Differences.Any(d => d.Conflict);
}

/// <summary>Identity is endpoint + client + route, independent of names and source serialization.</summary>
public static class NodeIdentity
{
    private static readonly string[] RouteKeys = ["sni", "host", "path", "serviceName", "flow", "encryption",
        "pbk", "sid", "mode", "alpn", "headerType", "mport", "obfs", "obfs-password", "extra", "ech", "pqv", "pinSHA256", "allowInsecure",
        "insecure", "pcs", "cert", "finalmask", "mux", "realm", "hopInterval"];
    public static string Address(string value) => IPAddress.TryParse(value.Trim('[', ']'), out var ip)
        ? ip.ToString() : value.Trim().TrimEnd('.').ToLowerInvariant();
    private static string Transport(string value) => value.ToLowerInvariant() is "tcp" or "raw" ? "tcp" : value.ToLowerInvariant();
    public static string Client(Node node)
    {
        if (node.Link.Length > 0) return Auth(ShareLink.Parse(node.Link).Auth);
        if (node.ClientId.Length > 0) return Auth(node.ClientId);
        if (node.Server.TryGetValue("客户端 UUID", out var id) && id.Length > 0) return Auth(id);
        if (!node.Server.TryGetValue("原始入站设置", out var raw)) return "";
        try
        {
            using var json = JsonDocument.Parse(raw);
            if (!json.RootElement.TryGetProperty("clients", out var list) || list.ValueKind != JsonValueKind.Array) return "";
            var clients = list.EnumerateArray().ToArray();
            return clients.Length == 1 && clients[0].TryGetProperty("id", out var value) ? Auth(value.ToString()) : "";
        }
        catch (JsonException) { return ""; }
    }
    private static string Auth(string value) => Guid.TryParse(value, out var id) ? id.ToString() : value;
    private static string Hidden(string value) => value.Length == 0 ? "未知" : "摘要 " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..10];

    // A missing client link does not erase route evidence already collected from the panel.
    // REALITY can admit several names/short IDs; each is an allowed alternative, not a new node.
    private static Dictionary<string, string[]> ServerRoutes(Node node)
    {
        Dictionary<string, string[]> result = [];
        JsonElement Read(string key)
        {
            if (!node.Server.TryGetValue(key, out var raw)) return default;
            try { using var document = JsonDocument.Parse(raw); return document.RootElement.Clone(); }
            catch (JsonException) { return default; }
        }
        JsonElement Get(JsonElement value, string key) => InventoryMapping.Get(value, key);
        string Text(JsonElement value, string key) => InventoryMapping.Text(value, key);
        void Add(string key, string value) { if (value.Length > 0) result[key] = [value]; }
        void Alternatives(string key, JsonElement array)
        {
            var values = InventoryMapping.Array(array).Select(InventoryMapping.Scalar).Where(v => v.Length > 0).Distinct().ToArray();
            if (values.Length > 0) result[key] = values;
        }
        var stream = Read("原始传输设置"); var settings = Read("原始入站设置");
        var client = Read("原始客户端设置");
        if (client.ValueKind != JsonValueKind.Object)
            client = InventoryMapping.Array(Get(settings, "clients")).FirstOrDefault(c => Auth(InventoryMapping.Auth(c)) == Client(node));
        if (Get(client, "flow").ValueKind == JsonValueKind.String)
            result["flow"] = [Store.Field(Text(client, "flow"), "未设置")];
        Add("encryption", Text(settings, "encryption"));
        if (!result.ContainsKey("encryption") && Text(settings, "decryption") == "none") Add("encryption", "none");
        var security = Store.Field(Text(stream, "security"), node.Security);
        var tls = Get(stream, security == "reality" ? "realitySettings" : "tlsSettings");
        var clientTls = Get(tls, "settings");
        Add("sni", Store.Field(Text(clientTls, "serverName"), Text(tls, "serverName")));
        if (security == "reality")
        {
            Alternatives("sni", Get(tls, "serverNames"));
            Alternatives("sid", Get(tls, "shortIds"));
            Add("pbk", Text(clientTls, "publicKey")); Add("pqv", Text(clientTls, "mldsa65Verify"));
        }
        Add("alpn", string.Join(',', InventoryMapping.Array(Get(tls, "alpn")).Select(InventoryMapping.Scalar)));
        Add("pcs", Text(clientTls, "verifyPeerCertByName"));
        Add("pinSHA256", Text(clientTls, "pinnedPeerCertSha256")); Add("ech", Text(clientTls, "echConfigList"));
        var network = Store.Field(Text(stream, "network"), node.Transport);
        var transport = Get(stream, network + "Settings");
        if (network is "tcp" or "raw")
        {
            if (transport.ValueKind != JsonValueKind.Object) transport = Get(stream, network == "tcp" ? "rawSettings" : "tcpSettings");
            if (stream.ValueKind == JsonValueKind.Object) Add("headerType", Store.Field(Text(Get(transport, "header"), "type"), "none"));
        }
        if (network is "ws" or "httpupgrade" or "xhttp")
        {
            Add("path", Text(transport, "path")); Add("host", Text(transport, "host"));
            if (network == "ws")
            {
                var headers = Get(transport, "headers");
                if (headers.ValueKind == JsonValueKind.Object)
                    Add("host", headers.EnumerateObject().FirstOrDefault(p => p.Name.Equals("Host", StringComparison.OrdinalIgnoreCase)).Value.ToString());
            }
        }
        if (network == "grpc") { Add("serviceName", Text(transport, "serviceName")); Add("mode", Text(transport, "mode")); }
        if (network == "xhttp")
        {
            Add("mode", Text(transport, "mode"));
            var extra = Get(transport, "extra");
            if (extra.ValueKind == JsonValueKind.Object) Add("extra", extra.GetRawText());
        }
        var finalmask = Get(stream, "finalmask");
        if (finalmask.ValueKind == JsonValueKind.Object) Add("finalmask", finalmask.GetRawText());
        return result;
    }

    public static NodeMatch Compare(Node left, Node right, Host[] hosts)
    {
        var a = left.Link.Length > 0 ? ShareLink.Parse(left.Link) : null;
        var b = right.Link.Length > 0 ? ShareLink.Parse(right.Link) : null;
        var serverA = a is null ? ServerRoutes(left) : [];
        var serverB = b is null ? ServerRoutes(right) : [];
        List<IdentityDifference> differences = [];
        var hardConflict = false;
        var routeConflict = false;
        void Field(string field, string x, string y, bool hard = false, bool secret = false)
        {
            if (x == y) return;
            var conflict = x.Length > 0 && y.Length > 0;
            if (conflict) { if (hard) hardConflict = true; else routeConflict = true; }
            differences.Add(new(field, secret ? Hidden(x) : x.Length == 0 ? "未知" : x,
                secret ? Hidden(y) : y.Length == 0 ? "未知" : y, conflict));
        }
        Field("VPS", left.HostId, right.HostId, true);
        Field("协议", left.Protocol.ToLowerInvariant(), right.Protocol.ToLowerInvariant(), true);
        Field("端口", left.Port == 0 ? "" : left.Port.ToString(), right.Port == 0 ? "" : right.Port.ToString(), true);
        Field("传输", Transport(left.Transport), Transport(right.Transport), true);
        Field("安全层", left.Security.ToLowerInvariant(), right.Security.ToLowerInvariant(), true);
        Field("IP 模式", left.IpMode, right.IpMode);
        var clientA = Client(left); var clientB = Client(right);
        Field("客户端身份", clientA, clientB, true, true);
        var sameHost = left.HostId.Length > 0 && left.HostId == right.HostId;
        if (sameHost) Field("入站", left.InboundId, right.InboundId, true);
        var host = hosts.FirstOrDefault(h => h.Id == Store.Field(left.HostId, right.HostId));
        var endpointA = Address(left.Address.Length == 0 && left.VpsVerified ? host?.Address ?? "" : left.Address);
        var endpointB = Address(right.Address.Length == 0 && right.VpsVerified ? host?.Address ?? "" : right.Address);
        var sameEndpoint = endpointA.Length > 0 && endpointA == endpointB;
        var aliases = new[] { host?.Address ?? "", host?.Ipv6 ?? "", left.Server.GetValueOrDefault("分享地址", ""), right.Server.GetValueOrDefault("分享地址", "") }
            .Where(x => x.Length > 0).Select(Address).ToHashSet();
        // Aliases require server evidence attached to this VPS, never DNS coincidence or matching names.
        var serverEvidence = host is not null && (left.VpsVerified || right.VpsVerified);
        var aliasEndpoint = serverEvidence && aliases.Contains(endpointA) && aliases.Contains(endpointB) &&
            !(IPAddress.TryParse(endpointA, out _) && IPAddress.TryParse(endpointB, out _));
        if (endpointA != endpointB)
            differences.Add(new("连接地址", endpointA.Length == 0 ? "未知" : endpointA, endpointB.Length == 0 ? "未知" : endpointB,
                endpointA.Length > 0 && endpointB.Length > 0 && !aliasEndpoint));
        // Unknown extensions cannot silently bypass route comparison.
        foreach (var key in RouteKeys.Concat(a?.Options.Keys.AsEnumerable() ?? []).Concat(b?.Options.Keys.AsEnumerable() ?? [])
            .Where(k => k is not ("type" or "security" or "sni")).Append("sni").Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] Values(ShareLink? link, Dictionary<string, string[]> server)
            {
                string[] values;
                if (link is null) values = server.GetValueOrDefault(key, []);
                else
                {
                    var value = link.Option(key);
                    if (key == "sni" && link.Security is "tls" or "reality") value = link.Sni;
                    if (key == "encryption" && link.IsVless) value = link.Option(key, "none");
                    if (key == "flow" && link.IsVless) value = link.Option(key, "未设置");
                    if (key == "headerType" && link.Transport is "tcp" or "raw") value = link.Option(key, "none");
                    if (key == "path" && link.Transport is "ws" or "httpupgrade") value = link.Option(key, "/");
                    values = [value];
                }
                return values.Select(value => key is "sni" or "host" ? Address(value) : value)
                    .Where(value => value.Length > 0).Distinct().ToArray();
            }
            var x = Values(a, serverA); var y = Values(b, serverB);
            if (x.Intersect(y, StringComparer.Ordinal).Any()) continue;
            Field(key, string.Join(" / ", x), string.Join(" / ", y), secret: ShareLink.IsSensitive(key) || key == "encryption");
        }
        if (left.Name != right.Name) differences.Add(new("备注名称", left.Name, right.Name, false));
        var knownClient = clientA.Length > 0 && clientA == clientB;
        var sameInbound = sameHost && left.InboundId.Length > 0 && left.InboundId == right.InboundId;
        var endpointKnown = sameEndpoint || aliasEndpoint || sameInbound && (endpointA.Length == 0 || endpointB.Length == 0);
        var comparable = sameInbound || (left.Port > 0 && left.Port == right.Port &&
            (sameEndpoint || aliasEndpoint || knownClient || sameHost));
        if (hardConflict)
            return new(left.Id, right.Id, "Different", "关键身份冲突，保留为不同节点。", [.. differences]);
        if (knownClient && endpointKnown && !routeConflict && left.Port > 0 && left.Port == right.Port)
            return new(left.Id, right.Id, "Same", "客户端身份一致，端点及已知路由兼容。", [.. differences]);
        return new(left.Id, right.Id, comparable ? "Review" : "Different",
            comparable ? "存在信息缺失或配置差异，请核对；明确冲突须先处理，再确认关联。" : "没有足够的共同身份依据。", [.. differences]);
    }

    public static NodeSource[] Sources(Node node)
    {
        List<NodeSource> values = [.. node.Sources];
        if (node.V2Id.Length > 0 && !values.Any(s => s.Kind == "v2rayN" && s.Key == node.V2Id && SameDirectory(s.Directory, node.V2Directory)))
            values.Add(new("v2rayN", node.V2Id, node.V2Directory, node.GroupId, node.Name, node.Link, node.SourceRemoved));
        if (node.VpsVerified && node.InboundId.Length > 0)
            values.Add(new("VPS SSH", node.HostId + "/" + node.InboundId + "/" + Client(node), Name: node.Name, Link: node.Link));
        values.Add(new("ledger", node.Id, Name: node.Name, Link: node.Link));
        return OrderedSources(values);
    }
    public static NodeSource[] OrderedSources(IEnumerable<NodeSource> values) => values
        .GroupBy(s => (s.Kind, s.Key, Directory: s.Directory.ToUpperInvariant())).Select(g => g.Last())
        .OrderBy(s => s.Kind, StringComparer.Ordinal).ThenBy(s => s.Key, StringComparer.Ordinal).ThenBy(s => s.Directory, StringComparer.OrdinalIgnoreCase).ToArray();
    public static bool SameDirectory(string a, string b) => string.Equals(a.TrimEnd('/', '\\'), b.TrimEnd('/', '\\'), StringComparison.OrdinalIgnoreCase);
    public static Node Merge(Node old, Node value, bool preferIncoming = false, bool associate = false)
    {
        var sources = OrderedSources(Sources(old).Concat(Sources(value)));
        if (associate)
        {
            NodeSource Snapshot(Node node)
            {
                var configuration = Wire.Encode(node with { Sources = [] });
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));
                return new("关联前台账", node.Id + "/" + hash, Name: node.Name, Link: node.Link, Configuration: configuration);
            }
            sources = OrderedSources(sources.Concat([Snapshot(old), Snapshot(value)]));
        }
        var server = new Dictionary<string, string>(old.Server);
        if (associate || value.InventoriedAt is null || old.InventoriedAt is null || value.InventoriedAt >= old.InventoriedAt)
            foreach (var item in value.Server)
                if (!associate || !server.TryGetValue(item.Key, out var prior) || string.IsNullOrWhiteSpace(prior)) server[item.Key] = item.Value;
        var related = new Dictionary<string, string>(old.Related);
        foreach (var item in value.Related)
            if (!associate || !related.TryGetValue(item.Key, out var prior) || string.IsNullOrWhiteSpace(prior)) related[item.Key] = item.Value;
        string Value(string prior, string incoming) => preferIncoming ? Store.Field(incoming, prior) : Store.Field(prior, incoming);
        return ShareLink.Normalize(old with
        {
            Name = old.Manual || associate ? old.Name : Store.Field(value.Name, old.Name),
            Link = preferIncoming ? Store.Field(value.Link, old.Link) : Store.Field(old.Link, value.Link),
            HostId = Store.Field(old.HostId, value.HostId), InboundId = associate ? Store.Field(old.InboundId, value.InboundId) : Store.Field(value.InboundId, old.InboundId),
            ClientId = Store.Field(Client(old), Client(value)),
            Address = Value(old.Address, value.Address), Port = preferIncoming && value.Port > 0 ? value.Port : old.Port > 0 ? old.Port : value.Port,
            Protocol = Value(old.Protocol, value.Protocol), Transport = Value(old.Transport, value.Transport), Security = Value(old.Security, value.Security),
            Notes = string.Join(Environment.NewLine, new[] { old.Notes, value.Notes }.Where(x => x.Length > 0).Distinct()),
            InventoriedAt = value.InventoriedAt > old.InventoriedAt || old.InventoriedAt is null ? value.InventoriedAt : old.InventoriedAt,
            VpsVerified = old.VpsVerified || value.VpsVerified, Manual = old.Manual || value.Manual,
            Paused = associate ? old.Paused || value.Paused : old.Paused,
            SourceRemoved = associate ? !old.Manual && !value.Manual && !old.VpsVerified && !value.VpsVerified &&
                sources.Any(s => s.Kind == "v2rayN") && sources.Where(s => s.Kind == "v2rayN").All(s => s.Removed) : old.SourceRemoved,
            V2Id = Store.Field(old.V2Id, value.V2Id), V2Directory = Store.Field(old.V2Directory, value.V2Directory), GroupId = Store.Field(old.GroupId, value.GroupId),
            Sources = sources, Source = Store.Field(string.Join(" + ", sources.Where(s => s.Kind is not ("ledger" or "关联前台账")).Select(s => s.Kind).Distinct()),
                Store.Field(old.Source, value.Source)),
            Server = server, Related = related
        });
    }
}
