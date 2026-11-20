using System.Net;
using System.Text.RegularExpressions;

namespace VPSnode.Core;

public static class Validation
{
    public static bool Address(string value) => IPAddress.TryParse(value, out _) ||
        (!value.StartsWith('-') && Uri.CheckHostName(value) == UriHostNameType.Dns);
    public static bool Port(int port) => port is >= 1 and <= 65535;
    public static bool WebUrl(string input) => Uri.TryCreate(input, UriKind.Absolute, out var url) &&
        (url.Scheme is "http" or "https") && url.UserInfo.Length == 0;
    public static bool Token(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}\z");

    private static void IdAndName(string id, string name)
    {
        if (!Token(id) || string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("缺少有效 ID 或名称。");
    }

    public static void Host(Host value)
    {
        IdAndName(value.Id, value.Name);
        if ((value.Address.Length > 0 && !Address(value.Address)) ||
            (value.Ipv6.Length > 0 && (!IPAddress.TryParse(value.Ipv6, out var v6) || v6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)) ||
            (!Port(value.SshPort)) ||
            (value.TunnelPort.HasValue && !Port(value.TunnelPort.Value)) ||
            (value.SshAlias.Length > 0 && !Token(value.SshAlias)) ||
            (value.User.Length > 0 && !Token(value.User)))
            throw new InvalidDataException("VPS 地址、SSH 账号、别名或端口无效。");
    }

    public static void Node(Node value)
    {
        IdAndName(value.Id, value.Name);
        if ((value.HostId.Length > 0 && !Token(value.HostId)) ||
            (value.Port != 0 && !Port(value.Port)) ||
            (value.Address.Length > 0 && !Address(value.Address)) ||
            value.IpMode is not ("auto" or "ipv4" or "ipv6"))
            throw new InvalidDataException("节点端口、地址或 IP 模式无效。");
    }

    public static void Web(WebTarget value)
    {
        IdAndName(value.Id, value.Name);
        if ((!WebUrl(value.Url) && value.Url.Length > 0) ||
            (value.Domain.Length > 0 && !Address(value.Domain)) ||
            (value.Sni.Length > 0 && !Address(value.Sni)) ||
            (value.ConnectAddress.Length > 0 && !Address(value.ConnectAddress)) ||
            !Port(value.Port) || (value.Url.Length == 0 && value.Domain.Length == 0 && value.Sni.Length == 0))
            throw new InvalidDataException("请检查 URL、域名、证书连接地址或端口。");
    }
}
