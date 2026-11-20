using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace VPSnode.Core;

public static class ProxyProbe
{
    private static readonly HashSet<string> VlessKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "security", "encryption", "path", "host", "mode", "extra", "headerType",
        "sni", "fp", "alpn", "flow", "pbk", "sid", "spx", "pqv", "allowInsecure", "insecure",
        "mux", "finalmask", "pcs", "pinSHA256"
    };

    private static readonly HashSet<string> HyKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "sni", "alpn", "allowInsecure", "insecure", "mport", "hopInterval", "upmbps", "downmbps",
        "obfs", "obfs-password", "pinSHA256", "fp"
    };

    public static string? FindCore(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory) ||
            !File.Exists(Path.Combine(directory, "v2rayN.exe"))) return null;
        foreach (var candidate in new[] { Path.Combine(directory, "bin", name, name + ".exe"),
                     Path.Combine(directory, "bin", name + ".exe") })
            if (File.Exists(candidate)) return candidate;
        var bin = Path.Combine(directory, "bin");
        return Directory.Exists(bin) ? Directory.EnumerateFiles(bin, name + ".exe", SearchOption.AllDirectories).FirstOrDefault() : null;
    }

    public static string? Unsupported(ShareLink link)
    {
        var keys = link.IsVless ? VlessKeys : HyKeys;
        var unknown = link.Options.Keys.Where(key => !keys.Contains(key)).ToArray();
        if (unknown.Length > 0) return "含当前核心适配器尚未验证的参数。";
        if (link.IsVless && (link.Transport is not ("tcp" or "raw" or "xhttp") ||
            link.Security is not ("none" or "tls" or "reality")))
            return "此传输或安全层组合尚未支持。";
        if (link.IsVless && link.Security == "reality" && link.Option("pbk").Length == 0)
            return "缺少 REALITY 客户端公钥参数。";
        if (!link.IsVless && (link.Option("pinSHA256").Length > 0 ||
            link.Option("obfs", "salamander") is not "salamander"))
            return "当前核心适配器无法无损使用此 Hysteria2 选项。";
        return null;
    }

    public static async Task<Observation> Run(Store store, Node node, bool speed, CancellationToken token)
    {
        var kind = speed ? "speed" : "node";
        var settings = store.Settings;
        var url = speed ? settings.SpeedUrl : settings.PingUrl;
        Observation Result(string state, string detail = "", double? value = null, string address = "") =>
            new(kind, node.Id, DateTimeOffset.Now, state, detail, value, node.IpMode + " · " + url, address);

        try
        {
            var directory = ClientDirectory(settings, node);
            if (directory.Length == 0)
                return Result("依赖缺失", "请启动已有的 v2rayN，或在设置中选择包含 v2rayN.exe 的目录。");
            string client;
            try { client = ClientLink(store, node, directory); }
            catch (InvalidDataException ex) { return Result("待补全", ex.Message); }
            var link = ShareLink.Parse(client);
            var unsupported = Unsupported(link);
            if (unsupported != null) return Result("参数待验证", unsupported);
            var coreName = link.IsVless ? "xray" : "sing-box";
            var core = FindCore(directory, coreName);
            if (core == null) return Result("依赖缺失", $"v2rayN 安装目录中缺少 {coreName}。");
            if (!Validation.WebUrl(url)) return Result("设置无效", "测试 URL 无效。");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(speed ? 45 : 18));
            var addr = await ShareLink.SelectAddress(link.Address, node.IpMode, timeout.Token);
            var port = FreePort();
            var json = Build(link, addr, port);
            var args = link.IsVless ? new[] { "run", "-config", "stdin:" } : ["run", "-c", "stdin"];
            using var process = ProcessRun.Start(core, args, Path.GetDirectoryName(core), input: true);
            using var job = ProcessJob.Attach(process);
            var drainOut = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            var drainErr = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            try
            {
                await process.StandardInput.WriteAsync(json.ToJsonString().AsMemory(), timeout.Token);
                process.StandardInput.Close();
                if (!await Ready(process, port, timeout.Token))
                    return Result("核心启动失败", "临时核心未就绪，请核对 v2rayN 核心版本。", address: addr);
                using var handler = new SocketsHttpHandler
                {
                    Proxy = new WebProxy($"socks5://127.0.0.1:{port}"), UseProxy = true,
                    AllowAutoRedirect = false, UseCookies = false
                };
                using var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                var sw = Stopwatch.StartNew();
                if (!speed)
                {
                    http.MaxResponseContentBufferSize = 1024 * 1024;
                    double[] samples = new double[2];
                    for (int i = 0; i < samples.Length; i++)
                    {
                        sw.Restart();
                        using var reply = await http.GetAsync(url, timeout.Token);
                        samples[i] = sw.Elapsed.TotalMilliseconds;
                        if ((int)reply.StatusCode != settings.PingStatus)
                            return Result("测试目标异常", $"第 {i + 1} 次 HTTP {(int)reply.StatusCode}", address: addr);
                        if (i == 0) await Task.Delay(100, timeout.Token);
                    }
                    return Result("可用", $"首次 {samples[0]:0.#} ms · 后续 {samples[1]:0.#} ms · 预热后单次完整请求", samples[1], addr)
                        with { Context = node.IpMode + " · " + url + " · 双请求" };
                }
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode)
                    return Result("测试目标异常", $"HTTP {(int)response.StatusCode}", address: addr);
                sw.Restart();
                var transferred = await Download(response, Math.Clamp(settings.SpeedBytes, 100_000, 100_000_000), timeout.Token);
                return transferred == 0 ? Result("测试目标异常", "没有下载到数据。", address: addr) :
                    Result("完成", $"{transferred:N0} B · {sw.Elapsed.TotalSeconds:F2} 秒",
                        transferred * 8 / (1_000_000d * Math.Max(.001, sw.Elapsed.TotalSeconds)), addr);
            }
            finally
            {
                ProcessRun.Stop(process);
                try { await Task.WhenAll(drainOut, drainErr); } catch (OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Result("超时", "代理请求或本机 DNS 超时。"); }
        catch (HttpRequestException) { return Result("连接失败", "代理链路或目标 HTTPS 请求失败。"); }
        catch (SocketException) { return Result("DNS 或连接失败", "无法解析地址或建立连接。"); }
        catch (InvalidDataException exception) { return Result("参数待验证", exception.Message); }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { return Result("检测失败", "核心无法启动或读取所需配置。"); }
    }

    public static string ClientDirectory(Settings settings, Node node)
    {
        if (settings.V2Directory.Length > 0) return settings.V2Directory;
        if (node.V2Directory.Length > 0 && File.Exists(Path.Combine(node.V2Directory, "v2rayN.exe"))) return node.V2Directory;
        return RunningV2Directory();
    }

    public static string ClientLink(Store store, Node node, string? directory = null)
    {
        if (node.Link.Length > 0) return node.Link;
        directory ??= ClientDirectory(store.Settings, node);
        try
        {
            var match = V2Sync.ProbeLink(directory, store.Hosts.FirstOrDefault(h => h.Id == node.HostId), node);
            if (match != null) return match;
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { throw new InvalidDataException("无法读取客户端配置。请检查 v2rayN 目录，或在节点编辑页粘贴分享链接。", ex); }
        throw new InvalidDataException("未找到唯一匹配的 v2rayN 线路。请在节点编辑页粘贴这条线路的分享链接后重试。");
    }
    private static string RunningV2Directory()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcessesByName("v2rayN"))
        {
            using (p)
            {
                try
                {
                    if (p.MainModule?.FileName is string exe && Path.GetDirectoryName(exe) is string path)
                        paths.Add(path);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
        }
        return paths.Count == 1 ? paths.First() : "";
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try { return ((IPEndPoint)listener.LocalEndpoint).Port; }
        finally { listener.Stop(); }
    }

    private static async Task<bool> Ready(Process process, int port, CancellationToken token)
    {
        for (int attempt = 0; attempt < 30 && !process.HasExited; attempt++)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, token);
                return !process.HasExited;
            }
            catch (SocketException) { await Task.Delay(100, token); }
        }
        return false;
    }

    private static async Task<int> Download(HttpResponseMessage response, int limit, CancellationToken token)
    {
        using var stream = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[32768];
        int count = 0;
        while (count < limit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit - count)), token);
            if (read == 0) break;
            count += read;
        }
        return count;
    }

    public static JsonObject Build(ShareLink link, string address, int bindPort) => link.IsVless
        ? XrayConfig(link, address, bindPort)
        : SingBoxConfig(link, address, bindPort);

    private static JsonObject XrayConfig(ShareLink link, string address, int bindPort)
    {
        var options = link.Option;
        var raw = link.Transport is "tcp" or "raw";
        JsonObject stream = new() { ["network"] = raw ? "raw" : "xhttp", ["security"] = link.Security };
        if (!raw)
        {
            JsonObject xhttp = new() { ["mode"] = options("mode", "auto"), ["path"] = options("path", "/"), ["host"] = options("host", "") };
            if (options("extra", "").Length > 0)
                xhttp["extra"] = JsonNode.Parse(options("extra", "")) as JsonObject
                    ?? throw new InvalidDataException("XHTTP extra 应为 JSON 对象。");
            stream["xhttpSettings"] = xhttp;
        }
        else if (options("headerType", "") == "http")
            stream["rawSettings"] = new JsonObject
            {
                ["header"] = new JsonObject
                {
                    ["type"] = "http",
                    ["request"] = new JsonObject
                    {
                        ["path"] = Split(options("path", "/")), ["headers"] = new JsonObject { ["Host"] = Split(options("host", "")) }
                    }
                }
            };
        if (link.Security == "reality")
        {
            JsonObject reality = new()
            {
                ["serverName"] = link.Sni, ["fingerprint"] = options("fp", "chrome"),
                ["publicKey"] = options("pbk", ""), ["shortId"] = options("sid", ""), ["spiderX"] = options("spx", "")
            };
            if (options("pqv", "").Length > 0) reality["mldsa65Verify"] = options("pqv", "");
            stream["realitySettings"] = reality;
        }
        if (link.Security == "tls")
        {
            JsonObject tls = new()
            {
                ["serverName"] = link.Sni, ["allowInsecure"] = Insecure(link)
            };
            if (options("alpn", "").Length > 0) tls["alpn"] = Split(options("alpn", ""));
            if (options("fp", "").Length > 0) tls["fingerprint"] = options("fp", "");
            if (options("pcs", "").Length > 0) tls["verifyPeerCertByName"] = options("pcs", "");
            if (options("pinSHA256", "").Length > 0) tls["pinnedPeerCertSha256"] = options("pinSHA256", "");
            stream["tlsSettings"] = tls;
        }
        if (options("finalmask", "").Length > 0) stream["finalmask"] = JsonNode.Parse(options("finalmask", ""));
        JsonObject user = new() { ["id"] = link.Auth, ["encryption"] = options("encryption", "none") };
        if (options("flow", "").Length > 0) user["flow"] = options("flow", "");
        JsonObject outbound = new()
        {
            ["protocol"] = "vless", ["tag"] = "proxy",
            ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject
                { ["address"] = address, ["port"] = link.Port, ["users"] = new JsonArray(user) }) },
            ["streamSettings"] = stream
        };
        if (options("mux", "") == "1") outbound["mux"] = new JsonObject { ["enabled"] = true };
        return new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray(new JsonObject { ["protocol"] = "socks", ["listen"] = "127.0.0.1", ["port"] = bindPort }),
            ["outbounds"] = new JsonArray(outbound)
        };
    }

    private static JsonObject SingBoxConfig(ShareLink link, string address, int bindPort)
    {
        JsonObject tls = new() { ["enabled"] = true, ["server_name"] = link.Sni, ["insecure"] = Insecure(link) };
        if (link.Option("alpn").Length > 0) tls["alpn"] = Split(link.Option("alpn"));
        JsonObject outbound = new()
        {
            ["type"] = "hysteria2", ["tag"] = "proxy", ["server"] = address, ["password"] = link.Auth, ["tls"] = tls
        };
        if (link.Option("mport").Length > 0)
            outbound["server_ports"] = Split(link.Option("mport").Replace('-', ':'));
        else outbound["server_port"] = link.Port;
        var interval = link.Option("hopInterval");
        if (interval.Length > 0)
        {
            var bounds = interval.Split('-', 2, StringSplitOptions.TrimEntries);
            static string Duration(string text) => uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out _)
                ? text + "s" : text;
            outbound["hop_interval"] = Duration(bounds[0]);
            if (bounds.Length == 2) outbound["hop_interval_max"] = Duration(bounds[1]);
        }
        if (int.TryParse(link.Option("upmbps"), CultureInfo.InvariantCulture, out var up) && up > 0) outbound["up_mbps"] = up;
        if (int.TryParse(link.Option("downmbps"), CultureInfo.InvariantCulture, out var down) && down > 0) outbound["down_mbps"] = down;
        if (link.Option("obfs-password").Length > 0)
            outbound["obfs"] = new JsonObject { ["type"] = link.Option("obfs", "salamander"), ["password"] = link.Option("obfs-password") };
        return new JsonObject
        {
            ["log"] = new JsonObject { ["level"] = "warn" },
            ["inbounds"] = new JsonArray(new JsonObject { ["type"] = "socks", ["listen"] = "127.0.0.1", ["listen_port"] = bindPort }),
            ["outbounds"] = new JsonArray(outbound), ["route"] = new JsonObject { ["final"] = "proxy" }
        };
    }

    private static bool Insecure(ShareLink link) =>
        link.Option("insecure", link.Option("allowInsecure")) is "1" or "true";

    private static JsonArray Split(string input) => new(input.Split(',', StringSplitOptions.RemoveEmptyEntries |
        StringSplitOptions.TrimEntries).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
