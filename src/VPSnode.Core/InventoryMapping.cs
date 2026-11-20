using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VPSnode.Core;

internal static class InventoryMapping
{
    internal static JsonElement Get(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var item) ? item : default;
    internal static string Text(JsonElement value, string key) => Scalar(Get(value, key));
    internal static string Scalar(JsonElement value) => value.ValueKind is JsonValueKind.String ? value.GetString() ?? "" : value.ValueKind is JsonValueKind.Number ? value.GetRawText() : "";
    internal static JsonElement[] Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [];
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..20];
    internal static string Auth(JsonElement client) => Store.Field(Text(client, "id"), Store.Field(Text(client, "auth"), Text(client, "password")));

    internal static string ClientLink(string protocol, string address, int port, string name, JsonElement settings, JsonElement stream, JsonElement client)
    {
        if (protocol != "vless" || Auth(client).Length == 0 || !Validation.Address(address)) return "";
        var network = Text(stream, "network");
        if (network.Length == 0) return "";
        var security = Text(stream, "security");
        Dictionary<string, string> options = new() { ["type"] = network, ["security"] = security.Length > 0 ? security : "none" };
        void Add(string key, string value) { if (value.Length > 0) options[key] = value; }
        var encryption = Text(settings, "encryption");
        if (encryption.Length == 0 && Text(settings, "decryption") != "none") return ""; // Never copy the server's private decryption key.
        Add("encryption", encryption.Length > 0 ? encryption : "none");
        Add("flow", Text(client, "flow"));
        var tls = Get(stream, security == "reality" ? "realitySettings" : "tlsSettings");
        var clientTls = Get(tls, "settings");
        var sni = Store.Field(Text(clientTls, "serverName"), Text(tls, "serverName"));
        if (sni.Length == 0 && security == "reality") sni = Array(Get(tls, "serverNames")).Select(Scalar).FirstOrDefault() ?? "";
        if (security is "tls" or "reality" && sni.Length == 0) return "";
        Add("sni", sni); Add("fp", Text(clientTls, "fingerprint"));
        Add("alpn", string.Join(',', Array(Get(tls, "alpn")).Select(Scalar)));
        if (security == "reality")
        {
            Add("pbk", Text(clientTls, "publicKey"));
            if (!options.ContainsKey("pbk")) return "";
            Add("sid", Array(Get(tls, "shortIds")).Select(Scalar).FirstOrDefault() ?? "");
            Add("spx", Text(clientTls, "spiderX")); Add("pqv", Text(clientTls, "mldsa65Verify"));
        }
        if (network == "xhttp")
        {
            var xhttp = Get(stream, "xhttpSettings");
            Add("path", Text(xhttp, "path")); Add("host", Text(xhttp, "host")); Add("mode", Text(xhttp, "mode"));
            var extra = Get(xhttp, "extra");
            if (extra.ValueKind == JsonValueKind.Object) Add("extra", extra.GetRawText());
        }
        else if (network is "tcp" or "raw")
        {
            var transport = Get(stream, network + "Settings");
            var header = Get(transport, "header");
            if (Text(header, "type") is not ("" or "none")) return ""; // Full request headers need the original client profile.
        }
        else return "";
        // Keep extensions in the server snapshot and require a client profile if they cannot be mapped losslessly.
        if (Text(clientTls, "echConfigList").Length > 0 || Get(stream, "sockopt").ValueKind == JsonValueKind.Object ||
            Get(stream, "finalmask").ValueKind == JsonValueKind.Object) return "";
        Add("pcs", Text(clientTls, "verifyPeerCertByName")); Add("pinSHA256", Text(clientTls, "pinnedPeerCertSha256"));
        return new ShareLink("vless", address, port, Auth(client), name, options).Format();
    }

    internal static string Compare(JsonElement settings, JsonElement stream, JsonElement generated)
    {
        if (generated.ValueKind != JsonValueKind.Object) return "启动文件未找到对应入站；运行内存配置未读取";
        List<string> differences = [];
        var gs = Get(generated, "settings"); var gt = Get(generated, "streamSettings");
        string NormalizeTransport(string value) => value == "raw" ? "tcp" : value;
        if (NormalizeTransport(Text(stream, "network")) != NormalizeTransport(Text(gt, "network"))) differences.Add("传输");
        if (Text(stream, "security") != Text(gt, "security")) differences.Add("安全层");
        if (Text(settings, "decryption") != Text(gs, "decryption")) differences.Add("服务端解密");
        string[] Clients(JsonElement value) => Array(Get(value, "clients"))
            .Where(c => Get(c, "enable").ValueKind != JsonValueKind.False && Text(c, "enable") != "0")
            .Select(c => Auth(c) + "|" + Text(c, "flow")).Order().ToArray();
        if (!Clients(settings).SequenceEqual(Clients(gs))) differences.Add("客户端身份/Flow");
        string[] Fallbacks(JsonElement value) => Array(Get(value, "fallbacks")).Select(f =>
            string.Join('|', new[] { "dest", "name", "alpn", "path" }.Select(k => Text(f, k))) + "|" + Store.Field(Text(f, "xver"), "0")).Order().ToArray();
        if (!Fallbacks(settings).SequenceEqual(Fallbacks(gs))) differences.Add("回落规则");
        var key = Text(stream, "security") == "reality" ? "realitySettings" : "tlsSettings";
        var a = Get(stream, key); var b = Get(gt, key);
        foreach (var field in new[] { "serverName", "target", "dest", "privateKey" })
            if (Text(a, field) != Text(b, field)) differences.Add(field);
        foreach (var field in new[] { "serverNames", "shortIds", "alpn" })
            if (!Array(Get(a, field)).Select(Scalar).Order().SequenceEqual(Array(Get(b, field)).Select(Scalar).Order())) differences.Add(field);
        string[] Certificates(JsonElement value) => Array(Get(value, "certificates"))
            .Select(c => Text(c, "certificateFile") + "|" + Text(c, "keyFile")).Order().ToArray();
        if (!Certificates(a).SequenceEqual(Certificates(b))) differences.Add("证书路径");
        if (Text(stream, "network") == "xhttp")
            foreach (var field in new[] { "path", "host", "mode" })
                if (Text(Get(stream, "xhttpSettings"), field) != Text(Get(gt, "xhttpSettings"), field)) differences.Add("XHTTP " + field);
        return differences.Count == 0 ? "已比较的关键字段一致；启动文件不等同运行内存配置" : "面板与启动文件差异：" + string.Join("、", differences) + "；运行内存配置未读取";
    }
}
