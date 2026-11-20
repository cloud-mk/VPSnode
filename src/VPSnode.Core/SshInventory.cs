using System.Net;
using System.Text;
using System.Text.Json;

namespace VPSnode.Core;

public static class SshInventory
{
    private const string TlsScript = """
        import socket,ssl,sys,json
        h,p,n=sys.argv[1:]
        try:
            with socket.create_connection((h,int(p)),timeout=5) as conn:
                with ssl.create_default_context().wrap_socket(conn,server_hostname=n) as tls:
                    print(json.dumps({'state':'TLS 正常','detail':tls.version() or 'TLS'}))
        except ssl.SSLCertVerificationError:
            print(json.dumps({'state':'证书校验失败','detail':'目标证书不受信任、已过期或名称不匹配'}))
        except ssl.SSLError:
            print(json.dumps({'state':'TLS 失败','detail':'握手失败'}))
        except socket.gaierror:
            print(json.dumps({'state':'DNS 失败','detail':'VPS 无法解析目标'}))
        except OSError:
            print(json.dumps({'state':'连接失败','detail':'VPS 到目标连接超时或被拒绝'}))
        """;

    private static string Executable => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "OpenSSH", "ssh.exe");

    public static string[] KnownAliases(string configPath)
    {
        try
        {
            return File.ReadLines(configPath)
                .Select(line => line.Split('#', 2)[0].Trim().Split([' ', '\t', '='], StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length > 1 && parts[0].Equals("Host", StringComparison.OrdinalIgnoreCase))
                .SelectMany(parts => parts.Skip(1)).Where(Validation.Token)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public static async Task<Host> FromAlias(string alias, CancellationToken token)
    {
        if (!Validation.Token(alias)) throw new InvalidDataException("SSH 别名无效。");
        var result = await ProcessRun.Capture(Executable, ["-G", "--", alias], token, 5);
        if (result.Exit != 0) throw new InvalidDataException("无法读取 Windows OpenSSH 配置。");
        var values = result.Output.Split('\n').Select(x => x.Trim().Split(' ', 2)).Where(x => x.Length == 2)
            .ToLookup(x => x[0], x => x[1]);
        string Read(string key) => values[key].FirstOrDefault() ?? "";
        return new()
        {
            Id = alias, Name = alias, SshAlias = alias, Address = Read("hostname"), User = Read("user"),
            KeyPath = Read("identityfile"), SshPort = int.TryParse(Read("port"), out var p) ? p : 22, Source = "Windows OpenSSH"
        };
    }

    private static string[] Arguments(Host host, string command)
    {
        Validation.Host(host);
        List<string> args = ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=6", "-o", "ForwardAgent=no",
            "-o", "UpdateHostKeys=no",
            "-o", "ClearAllForwardings=yes", "-o", "ControlMaster=no", "-o", "RemoteCommand=none"];
        if (host.PasswordCipher.Length > 0)
        {
            args.AddRange(["-o", "StrictHostKeyChecking=accept-new"]);
            args[args.IndexOf("BatchMode=yes")] = "BatchMode=no";
            args.AddRange(["-o", "PreferredAuthentications=password", "-o", "NumberOfPasswordPrompts=1"]);
        }
        else args.AddRange(["-o", "StrictHostKeyChecking=no", "-o", "UserKnownHostsFile=NUL", "-o", "GlobalKnownHostsFile=NUL"]);
        if (host.SshAlias.Length > 0) args.AddRange(["--", host.SshAlias, command]);
        else
        {
            args.AddRange(["-p", host.SshPort.ToString()]);
            if (host.KeyPath.Length > 0) args.AddRange(["-o", "IdentitiesOnly=yes", "-i", host.KeyPath]);
            if (host.User.Length > 0) args.AddRange(["-l", host.User]);
            args.AddRange(["--", host.Address, command]);
        }
        return [.. args];
    }

    private static string Python(string script, params string[] values)
    {
        var code = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        var quoted = string.Join(" ", values.Select(x => "'" + x.Replace("'", "'\\''") + "'"));
        return $"python3 -c 'import base64;exec(base64.b64decode(\"{code}\"))' {quoted}";
    }

    private sealed record Remote(string Hostname, string Hardware, string Os, Listener[] Ports,
        Inbound[] Inbounds, bool InboundsUnavailable, int? PanelPort, string PanelPath, string PanelScheme,
        bool ListenersAvailable, string DatabasePath, string[] Processes, string[] Errors);
    private sealed record Inbound(string Id, string Name, bool Enabled, string Listen, int Port, string Protocol,
        string Target, string ServerNames, string Transport, string Security, JsonElement Settings, JsonElement Stream,
        JsonElement[] Clients, string ClientSource, string ShareAddress, string ShareStrategy, JsonElement Generated, string[] Issues);
    private sealed record TlsResult(string State, string Detail);

    public static async Task<Observation> Probe(Store store, Host host, bool inventory, CancellationToken token)
    {
        Observation Answer(string state, string detail = "") => new("host", host.Id, DateTimeOffset.Now, state, detail, Context: "SSH");
        if (host.SshAlias.Length == 0 && host.Address.Length == 0) return Answer("待配置", "需要 SSH 别名或地址。");
        try
        {
            var command = "true";
            if (inventory)
            {
                var probe = Python(InventoryScript.Python);
                // Use the existing account's non-interactive rights only for this fixed inventory script.
                command = $"runner=''; if [ ! -r /etc/x-ui/x-ui.db ] && sudo -n test -r /etc/x-ui/x-ui.db 2>/dev/null && sudo -n -l python3 >/dev/null 2>&1; then runner='sudo -n'; fi; $runner {probe}";
            }
            var result = await ProcessRun.Capture(Executable, Arguments(host, command), token, passwordCipher: host.PasswordCipher);
            if (result.Exit != 0)
            {
                if (result.Exit is 126 or 127) return Answer("SSH 可登录", "VPS 缺少可用的 Python 3 盘点环境。");
                if (result.Error.Contains("UNPROTECTED PRIVATE KEY FILE", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("bad permissions", StringComparison.OrdinalIgnoreCase) ||
                    result.Error.Contains("Bad owner or permissions", StringComparison.OrdinalIgnoreCase))
                    return Answer("密钥权限异常", "Windows OpenSSH 拒绝使用当前文件权限的私钥。");
                if (result.Error.Contains("Could not resolve hostname", StringComparison.OrdinalIgnoreCase))
                    return Answer("SSH 地址无法解析", "请核对 Windows OpenSSH 别名或服务器地址。");
                if (result.Error.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                    return Answer("认证失败", "SSH 可达，账号或凭据校验失败。");
                return Answer("SSH 连接失败", "入口不可达或远程只读命令未完成。");
            }
            if (!inventory) return Answer("SSH 可登录");
            var remote = Wire.Decode<Remote>(result.Output);
            var currentHost = store.Hosts.FirstOrDefault(x => x.Id == host.Id) ?? host;
            var now = DateTimeOffset.Now;
            var updatedHost = currentHost with
            {
                Hostname = remote.Hostname, Hardware = remote.Hardware,
                Os = remote.Os, Ports = remote.ListenersAvailable ? remote.Ports : currentHost.Ports, InventoriedAt = now,
                RemotePanelPort = remote.InboundsUnavailable ? currentHost.RemotePanelPort : remote.PanelPort,
                RemotePanelPath = remote.InboundsUnavailable ? currentHost.RemotePanelPath : remote.PanelPath,
                PanelScheme = remote.InboundsUnavailable ? currentHost.PanelScheme : remote.PanelScheme
            };
            var existingNodes = store.Nodes.Where(n => n.HostId == host.Id).ToArray();
            List<Node> nodes = [];
            foreach (var inbound in remote.Inbounds)
            {
                var clients = inbound.Clients.Length > 0 ? inbound.Clients : [default(JsonElement)];
                foreach (var client in clients)
                {
                    var auth = InventoryMapping.Auth(client);
                    var sameInbound = existingNodes.Where(n => n.InboundId == inbound.Id).ToArray();
                    var existing = sameInbound.FirstOrDefault(n => auth.Length > 0 && n.ClientId == auth)
                        ?? (clients.Length == 1 ? sameInbound.FirstOrDefault(n => n.ClientId.Length == 0 && n.Link.Length == 0) : null);
                    var server = new Dictionary<string, string>
                    {
                        ["入站名称"] = inbound.Name, ["启用"] = inbound.Enabled ? "是" : "否", ["监听"] = inbound.Listen,
                        ["入站端口"] = inbound.Port.ToString(), ["协议"] = inbound.Protocol, ["传输"] = inbound.Transport,
                        ["安全层"] = inbound.Security, ["REALITY 目标"] = inbound.Target, ["允许的 SNI"] = inbound.ServerNames,
                        ["原始入站设置"] = inbound.Settings.GetRawText(), ["原始传输设置"] = inbound.Stream.GetRawText(),
                        ["回落"] = InventoryMapping.Get(inbound.Settings, "fallbacks").ValueKind == JsonValueKind.Array
                            ? InventoryMapping.Get(inbound.Settings, "fallbacks").GetRawText() : "[]",
                        ["分享地址"] = inbound.ShareStrategy == "custom" ? inbound.ShareAddress : "",
                        ["分享地址策略"] = inbound.ShareStrategy, ["客户端 UUID"] = auth,
                        ["客户端来源"] = inbound.ClientSource, ["配置来源"] = remote.DatabasePath + " · SQLite 只读事务；未调用面板 API",
                        ["配置状态"] = inbound.Issues.Length == 0 ? "面板已配置" : "本次读取不完整；核对采集问题",
                        ["配置比对"] = InventoryMapping.Compare(inbound.Settings, inbound.Stream, inbound.Generated),
                        ["进程监听"] = !remote.ListenersAvailable ? "本次读取失败；不能使用旧监听证明当前状态" :
                            remote.Ports.Any(p => p.Port == inbound.Port && p.Service.StartsWith("xray", StringComparison.OrdinalIgnoreCase))
                                ? "观察到 Xray 监听；代理可用性须另行实测" : "未观察到对应 Xray 监听；不代表代理已停用",
                        ["运行内存配置"] = "未读取；进程 PID " + string.Join(",", remote.Processes),
                        ["采集问题"] = string.Join("；", remote.Errors.Concat(inbound.Issues)),
                        ["采集时间"] = now.ToString("O"),
                        ["客户端启用"] = InventoryMapping.Get(client, "enable").ValueKind == JsonValueKind.False || InventoryMapping.Text(client, "enable") == "0" ? "否" : auth.Length > 0 ? "是" : "未知"
                    };
                    if (client.ValueKind == JsonValueKind.Object) server["原始客户端设置"] = client.GetRawText();
                    if (inbound.Generated.ValueKind == JsonValueKind.Object) server["原始启动文件入站"] = inbound.Generated.GetRawText();
                    var address = inbound.ShareStrategy == "custom" ? inbound.ShareAddress : host.Address;
                    var name = clients.Length > 1 ? inbound.Name + " · " + Store.Field(InventoryMapping.Text(client, "email"), InventoryMapping.Hash(auth)[..6]) : inbound.Name;
                    var link = InventoryMapping.ClientLink(inbound.Protocol, address, inbound.Port, name, inbound.Settings, inbound.Stream, client);
                    server["客户端配置"] = link.Length > 0 ? "由面板持久化字段生成；尚须代理实测" : "字段缺失或扩展参数尚不支持；需客户端配置补全";
                    var id = host.Id + "-in-" + inbound.Id + (auth.Length > 0 ? "-" + InventoryMapping.Hash(auth) : "");
                    nodes.Add((existing ?? new Node { Id = id, Name = name }) with
                    {
                        HostId = host.Id, InboundId = inbound.Id, ClientId = auth, VpsVerified = true, Source = "VPS SSH", InventoriedAt = now,
                        Protocol = inbound.Protocol == "vless" ? "VLESS" : inbound.Protocol, Port = inbound.Port,
                        Address = Validation.Address(address) ? address : "", Link = link,
                        Transport = inbound.Transport, Security = inbound.Security, Server = server
                    });
                }
            }
            store.Import(new(1, [updatedHost], [.. nodes], []), apply: true, enqueue: false);
            // Retain IDs, credentials and observations of absent records, but invalidate their freshness.
            // A failed/partial read is never evidence that an inbound or a client was deleted.
            foreach (var old in store.Nodes.Where(n => n.HostId == host.Id && n.VpsVerified))
            {
                if (nodes.Any(n => n.InboundId == old.InboundId && n.ClientId == old.ClientId)) continue;
                var partial = remote.InboundsUnavailable || remote.Errors.Length > 0 ||
                    remote.Inbounds.Any(i => i.Id == old.InboundId && i.Issues.Length > 0);
                store.Save(old with { Server = new(old.Server)
                {
                    ["配置状态"] = partial ? "本次读取失败；保留历史" : "本次未发现；保留历史",
                    ["最近核查时间"] = now.ToString("O")
                } });
            }
            return Answer("SSH 可登录", remote.InboundsUnavailable
                ? "已更新主机与监听。3x-ui 入站数据库无法读取。"
                : $"已读取 {remote.Inbounds.Length} 条入站、{nodes.Count} 条客户端/待补全记录；代理可用性尚须实测。" +
                    (remote.Errors.Length > 0 || remote.Inbounds.Any(i => i.Issues.Length > 0)
                        ? " 采集存在问题：" + string.Join("；", remote.Errors.Concat(remote.Inbounds.SelectMany(i => i.Issues))) : ""));
        }
        catch (TimeoutException) { return Answer("SSH 超时", "登录或盘点未在时限内完成。"); }
        catch (Exception ex) when (ex is IOException or JsonException or System.ComponentModel.Win32Exception)
        { return Answer("盘点失败", "Windows OpenSSH 或远端结构化盘点不可用。"); }
    }

    public static async Task<Observation> CheckTarget(Host? host, Node node, ShareLink link, CancellationToken token)
    {
        const string kind = "sni-vps";
        Observation Answer(string state, string detail, string address = "") =>
            new(kind, node.Id, DateTimeOffset.Now, state, detail, Address: address);
        if (host is null || (host.Address.Length == 0 && host.SshAlias.Length == 0))
            return Answer("未检测", "缺少 VPS 的 SSH 入口。");
        var target = node.Server.GetValueOrDefault("REALITY 目标", "");
        if (!Uri.TryCreate("https://" + target, UriKind.Absolute, out var uri) ||
            !Validation.Address(uri.Host.Trim('[', ']')) || !Validation.Address(link.Sni) ||
            uri.UserInfo.Length > 0 || uri.AbsolutePath != "/" || uri.Query.Length > 0)
            return Answer("未检测", "未记录可通过 TCP 检测的 REALITY 握手目标。");
        try
        {
            var result = await ProcessRun.Capture(Executable,
                Arguments(host, Python(TlsScript, uri.Host.Trim('[', ']'), uri.Port.ToString(), link.Sni)), token, passwordCipher: host.PasswordCipher);
            if (result.Exit != 0) return Answer("未检测", "SSH 登录或 VPS TLS 探测工具不可用。", target);
            var tls = Wire.Decode<TlsResult>(result.Output);
            return Answer(tls.State, tls.Detail, target);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or System.ComponentModel.Win32Exception)
        { return Answer("检测失败", "VPS 探测超时或未返回可读结果。", target); }
    }
}
