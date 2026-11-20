using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VPSnode.Core;

public sealed record ShareLink(string Scheme, string Address, int Port, string Auth, string Name,
    Dictionary<string, string> Options)
{
    public string Option(string key, string otherwise = "") =>
        Options.TryGetValue(key, out var value) && value.Length > 0 ? value : otherwise;

    public bool IsVless => Scheme == "vless";
    public string Transport => IsVless ? Option("type", "tcp") : "quic";
    public string Security => IsVless ? Option("security", "none") : "tls";
    public string Protocol => IsVless ? "VLESS" : "Hysteria2";
    public string Sni => Option("sni", Address);

    public static ShareLink Parse(string input)
    {
        var match = Regex.Match(input.Trim(),
            @"\A(?<scheme>vless|hysteria2|hy2)://(?<auth>[^/?#@]+)@(?<host>\[[^\]]+\]|[^:/?#]+)(?::(?<port>[^/?#]+))?/?(?:\?(?<args>[^#]*))?(?:#(?<name>.*))?\z",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        if (!match.Success || input.Length > 20000) throw new InvalidDataException("代理分享链接无效或协议不支持。");
        var scheme = match.Groups["scheme"].Value.ToLowerInvariant();
        var address = match.Groups["host"].Value.Trim('[', ']');
        var portField = match.Groups["port"].Value;
        var auth = Uri.UnescapeDataString(match.Groups["auth"].Value);
        var port = 443;
        Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
        foreach (var part in match.Groups["args"].Value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            if (!options.TryAdd(Uri.UnescapeDataString(split[0]),
                split.Length == 2 ? Uri.UnescapeDataString(split[1]) : ""))
                throw new InvalidDataException("链接含重复参数。");
        }
        if (portField.Length != 0 && int.TryParse(portField, out port) && !Validation.Port(port))
            throw new InvalidDataException("节点端口须为 1 至 65535。");
        if (portField.Length != 0 && !int.TryParse(portField, out port))
        {
            if (scheme == "vless" || !Regex.IsMatch(portField, @"\A[0-9,:-]+\z") ||
                !options.TryAdd("mport", portField))
                throw new InvalidDataException("节点端口无效。");
            port = 0;
        }
        if (!Validation.Address(address) || (port != 0 && !Validation.Port(port)) || auth.Length == 0)
            throw new InvalidDataException("节点地址、端口或凭据缺失。");
        return new(scheme == "hy2" ? "hysteria2" : scheme, address, port, auth,
            Uri.UnescapeDataString(match.Groups["name"].Value), options);
    }

    public string Format()
    {
        var host = Address.Contains(':') ? $"[{Address}]" : Address;
        var query = string.Join("&", Options.Select(x => Escape(x.Key) + "=" + Escape(x.Value)));
        var port = Port > 0 ? Port.ToString() : Option("mport");
        if (Port == 0) query = string.Join("&", Options.Where(x => x.Key != "mport")
            .Select(x => Escape(x.Key) + "=" + Escape(x.Value)));
        return $"{Scheme}://{Escape(Auth)}@{host}:{port}" + (query.Length > 0 ? "?" + query : "") +
            (Name.Length > 0 ? "#" + Escape(Name) : "");
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    public static Node Normalize(Node node)
    {
        if (node.Link.Length == 0) return node;
        var profile = Parse(node.Link);
        return node with
        {
            Name = Store.Field(node.Name, Store.Field(profile.Name, profile.Address)),
            Address = profile.Address, Port = profile.Port, Transport = profile.Transport,
            Security = profile.Security, Protocol = profile.Protocol
        };
    }

    public static Node New(string link, string hostId = "", string? id = null)
    {
        var profile = Parse(link);
        var canonical = (profile with
        {
            Name = "",
            Options = profile.Options.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value)
        }).Format();
        return Normalize(new()
        {
            Id = id ?? "node-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostId + "|" + canonical)))[..24],
            Name = Store.Field(profile.Name, profile.Address),
            HostId = hostId, Link = link.Trim(), Manual = true, Source = "分享链接"
        });
    }

    public static bool IsSensitive(string key) =>
        new[] { "private", "password", "passwd", "auth", "secret", "token", "uuid", "原始", "密钥", "密码", "凭据", "salamander", "shortid", "short id", "sid", "extra" }
            .Any(word => key.Contains(word, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<DisplayField> Fields()
    {
        yield return new("协议", Protocol, false);
        yield return new("连接地址", Address, false);
        yield return new("端口", Port == 0 ? Option("mport") : Port.ToString(), false);
        yield return new("传输", Transport, false);
        yield return new("安全层", Security, false);
        yield return new(IsVless ? "客户端 UUID / ID" : "认证密码", Auth, true);
        if (IsVless)
        {
            var encryption = Option("encryption", "none");
            var parts = encryption.Split('.');
            var known = parts.Length >= 4 && parts[0] == "mlkem768x25519plus";
            yield return new("VLESS 加密", encryption == "none" ? "关闭" : known ? "启用 · ML-KEM-768 + X25519" : "启用 · 机制待核对", false);
            if (known)
            {
                if (parts[1] is "native" or "xorpub" or "random") yield return new("加密模式", parts[1], false);
                if (parts[2] is "0rtt" or "1rtt") yield return new("握手模式", parts[2].ToUpperInvariant(), false);
            }
            yield return new("认证方式", Guid.TryParse(Auth, out _) ? "UUID" : "客户端 ID", false);
            yield return new("流控 Flow", Option("flow", "未设置"), false);
        }
        foreach (var (key, value) in Options.OrderBy(x => x.Key))
        {
            if (IsVless && (key == "flow" || key == "encryption" && value == "none")) continue;
            yield return new(key == "encryption" ? "encryption 原值" : key, value, IsSensitive(key) || key == "encryption");
        }
    }

    public static async Task<string> SelectAddress(string host, string mode, CancellationToken token)
    {
        if (mode == "auto") return host;
        var family = mode == "ipv4" ? System.Net.Sockets.AddressFamily.InterNetwork :
            System.Net.Sockets.AddressFamily.InterNetworkV6;
        var addresses = await Dns.GetHostAddressesAsync(host, family, token);
        return addresses.FirstOrDefault()?.ToString() ?? throw new InvalidDataException("目标无对应 IP 版本。");
    }
}
