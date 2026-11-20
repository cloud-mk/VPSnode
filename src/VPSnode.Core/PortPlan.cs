namespace VPSnode.Core;

public sealed record PortUse(string Protocol, int Port, string Purpose, string Addresses, string State);

public sealed class PortPlan
{
    public PortUse[] Used { get; }
    public PortUse[] Unidentified { get; }
    public string[] Candidates { get; }
    public bool HasSnapshot { get; }

    private static readonly (string Protocol, int Port)[] Common = [("TCP",80),("TCP",443),("TCP",2053),("TCP",2083),("TCP",2096),
        ("TCP",8080),("TCP",8443),("UDP",443),("UDP",8443)];

    public PortPlan(Host host, IEnumerable<Node> nodes, IEnumerable<WebTarget>? websites = null)
    {
        var purposes = new Dictionary<(string Protocol, int Port), List<string>>();
        void Reserve(string protocol, int port, string purpose)
        {
            if (!Validation.Port(port)) return;
            if (!purposes.TryGetValue((protocol, port), out var labels)) purposes[(protocol, port)] = labels = [];
            if (!labels.Contains(purpose)) labels.Add(purpose);
        }
        Reserve("TCP", host.SshPort, "SSH 登录");
        if (host.RemotePanelPort is int panel) Reserve("TCP", panel, "3x-ui 面板");
        var fallbackSites = (websites ?? []).Where(w => w.HostId.Length == 0 || w.HostId == host.Id)
            .SelectMany(w => w.FallbackNodeIds.Select(id => (NodeId: id, w.Name))).ToLookup(x => x.NodeId, x => x.Name);
        foreach (var n in nodes.Where(n => n.HostId == host.Id && n.VpsVerified))
        {
            var port = int.TryParse(n.Server.GetValueOrDefault("入站端口"), out var serverPort) ? serverPort : n.Port;
            var protocol = n.Protocol.Equals("Hysteria2", StringComparison.OrdinalIgnoreCase) ? "UDP" : "TCP";
            Reserve(protocol, port, n.Name);
            foreach (var site in fallbackSites[n.Id]) Reserve(protocol, port, $"回落网站 · {site}");
        }
        var listeners = host.Ports.GroupBy(p => (Protocol: p.Protocol.ToUpperInvariant(), p.Port))
            .ToDictionary(g => g.Key, g => g.ToArray());
        HasSnapshot = host.InventoriedAt.HasValue && host.Ports.Length > 0;
        List<PortUse> used = [];
        List<PortUse> unidentified = [];
        foreach (var key in listeners.Keys.Union(purposes.Keys).OrderBy(k => k.Port).ThenBy(k => k.Protocol))
        {
            var rows = listeners.GetValueOrDefault(key) ?? [];
            var labels = purposes.GetValueOrDefault(key) ?? [];
            var services = rows.Select(p => p.Service).Where(s => s.Length > 0).Distinct().ToArray();
            // Dedicated DNS servers remain visible even on port 53.
            if (labels.Count == 0 && (key.Port is 53 or 68 or 546 or 123 or 323 or 5353 or 5355) &&
                services.All(s => s.StartsWith("systemd-", StringComparison.Ordinal) ||
                    s is "dhclient" or "chronyd" or "avahi-daemon")) continue;
            if (labels.Count == 0 && key.Port == 111 && services.Length > 0 && services.All(s => s == "rpcbind")) continue;
            if (labels.Count == 0 && services.Any(s => s is "nginx" or "caddy" or "apache2" or "httpd" ||
                (key.Protocol == "TCP" && key.Port == 80 && s is "python3" or "node"))) labels.Add("网站服务");
            var usage = string.Join(" · ", labels.Concat(services).Distinct());
            if (usage.Length == 0) usage = "监听进程未识别";
            var list = labels.Count == 0 && services.Length == 0 && !Common.Contains(key) ? unidentified : used;
            list.Add(new(key.Protocol, key.Port, usage, string.Join("、", rows.Select(r => r.Address).Distinct()),
                rows.Length > 0 ? "已监听" : HasSnapshot ? "已登记 · 未见监听" : "已登记 · 待盘点"));
        }
        Used = [.. used];
        Unidentified = [.. unidentified];
        Candidates = !HasSnapshot ? [] : Common.Where(k => !listeners.ContainsKey(k) && !purposes.ContainsKey(k))
            .Select(k => $"{k.Protocol} {k.Port}").ToArray();
    }
}
