using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DnsClient;
using DnsClient.Protocol;

namespace VPSnode.Core;

public static class WebProbe
{
    private static readonly LookupClient DnsRecords = new(new LookupClientOptions { Timeout = TimeSpan.FromSeconds(2), Retries = 0 });
    public static string Domain(WebTarget target) => Store.Field(target.Domain,
        Store.Field(target.Sni, Uri.TryCreate(target.Url, UriKind.Absolute, out var uri) ? uri.IdnHost : ""));
    public static string Address(WebTarget target) => Store.Field(target.ConnectAddress, Domain(target));
    public static string Name(WebTarget target) => Store.Field(target.Sni, Domain(target));

    internal static string PublicId(string hostId, string domain, int port) =>
        "tls-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(hostId + "|" + domain.ToLowerInvariant() + "|" + port)))[..30];

    // A public entry and a certificate name can differ (CDN, origin or a separate TLS SNI).
    public static bool UsesEndpoint(Node node, WebTarget site)
    {
        if (node.HostId.Length > 0 && site.HostId.Length > 0 && node.HostId != site.HostId) return false;
        if (node.Link.Length == 0 || node.Port != site.Port || node.Security != "tls") return false;
        var link = ShareLink.Parse(node.Link);
        return string.Equals(link.Address.TrimEnd('.'), Address(site).TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            && string.Equals(link.Sni.TrimEnd('.'), Name(site).TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasFallbackRules(Node node)
    {
        if (!node.VpsVerified || !node.Server.TryGetValue("回落", out var raw)) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0;
        }
        catch (System.Text.Json.JsonException) { return false; }
    }

    public static bool IsRelated(Node node, WebTarget site) =>
        site.CertificateNodeIds.Contains(node.Id) || site.FallbackNodeIds.Contains(node.Id) || UsesEndpoint(node, site);

    internal static WebTarget ForStorage(WebTarget site) => site with
    {
        FallbackNodeIds = site.FallbackNodeIds.Except(site.AutoFallbackNodeIds).ToArray(),
        AutoFallbackNodeIds = []
    };

    // This confirms a configured route only, never the availability of the backend or proxy.
    public static bool ConfiguredFallback(Node node, WebTarget site)
    {
        if (!HasFallbackRules(node) || !UsesEndpoint(node, site) ||
            node.Server.GetValueOrDefault("配置状态") != "面板已配置" ||
            !node.Server.TryGetValue("原始传输设置", out var stream)) return false;
        try
        {
            using var settings = System.Text.Json.JsonDocument.Parse(stream);
            var tls = InventoryMapping.Get(settings.RootElement, "tlsSettings");
            var domain = Name(site);
            if (!string.Equals(InventoryMapping.Text(tls, "serverName").TrimEnd('.'), domain.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)) return false;
            using var rules = System.Text.Json.JsonDocument.Parse(node.Server["回落"]);
            foreach (var rule in rules.RootElement.EnumerateArray())
            {
                var name = InventoryMapping.Text(rule, "name");
                var path = InventoryMapping.Text(rule, "path");
                var dest = InventoryMapping.Text(rule, "dest");
                if (dest.Length == 0 || (name.Length > 0 && !name.Equals(domain, StringComparison.OrdinalIgnoreCase)) || path.Length > 0) continue;
                var alpn = InventoryMapping.Text(rule, "alpn");
                var tlsAlpn = InventoryMapping.Array(InventoryMapping.Get(tls, "alpn")).Select(InventoryMapping.Scalar).ToArray();
                // An ALPN-specific rule does not prove that other browser-negotiated protocols use this backend.
                if (alpn.Length > 0 && (tlsAlpn.Length != 1 || tlsAlpn[0] != alpn)) continue;
                return true;
            }
        }
        catch (System.Text.Json.JsonException) { }
        return false;
    }

    public static WebTarget[] Targets(Store store)
    {
        var registered = store.Websites.ToList();
        var automatic = new HashSet<string>();
        var nodes = store.Nodes;
        bool Same(string a, string b) => a.TrimEnd('.').Equals(b.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
        bool Endpoint(WebTarget a, WebTarget b) => Same(Domain(a), Domain(b)) && Same(Address(a), Address(b)) &&
            Same(Name(a), Name(b)) && a.Port == b.Port && a.Location == b.Location && a.Url == b.Url &&
            a.CheckWebsite == b.CheckWebsite && a.ExpectedStatus == b.ExpectedStatus && a.Contains == b.Contains && a.Enabled == b.Enabled;
        foreach (var node in nodes.Where(n => n.Security.Equals("tls", StringComparison.OrdinalIgnoreCase) && n.Link.Length > 0))
        {
            var link = ShareLink.Parse(node.Link);
            var domain = link.Sni;
            if (!Validation.Address(domain) || IPAddress.TryParse(domain, out _)) continue;
            var candidate = new WebTarget
            {
                Id = PublicId(node.HostId, domain, node.Port), Name = domain, HostId = node.HostId,
                Domain = domain, Port = node.Port == 0 ? 443 : node.Port,
                CertificateNodeIds = [node.Id], Location = "公网", Enabled = !node.Paused && !node.SourceRemoved
            };
            // Host identity is metadata, not a second copy of an otherwise identical public endpoint.
            var matching = registered.Where(t => (t.HostId.Length == 0 || node.HostId.Length == 0 || t.HostId == node.HostId) &&
                Same(Domain(t), domain) && Same(Address(t), domain) && Same(Name(t), domain) && t.Port == candidate.Port).ToArray();
            var explicitTarget = matching.FirstOrDefault(t => t.HostId == node.HostId) ?? matching.FirstOrDefault();
            if (explicitTarget is not null)
            {
                var index = registered.IndexOf(explicitTarget);
                registered[index] = explicitTarget with
                {
                    HostId = Store.Field(explicitTarget.HostId, node.HostId),
                    CertificateNodeIds = explicitTarget.CertificateNodeIds.Append(node.Id).Distinct().ToArray(),
                    Enabled = explicitTarget.Enabled || (automatic.Contains(explicitTarget.Id) && candidate.Enabled)
                };
                continue;
            }
            if (registered.Any(t => t.Id == candidate.Id)) candidate = candidate with { Id = candidate.Id + "-public" };
            automatic.Add(candidate.Id); registered.Add(candidate);
        }
        // Consolidate exact duplicate registrations in the view without deleting IDs or their histories.
        List<WebTarget> result = [];
        foreach (var site in registered)
        {
            var existing = result.FirstOrDefault(t => (t.HostId.Length == 0 || site.HostId.Length == 0 || t.HostId == site.HostId) && Endpoint(t, site));
            if (existing is null) { result.Add(site); continue; }
            var index = result.IndexOf(existing);
            result[index] = existing with
            {
                HostId = Store.Field(existing.HostId, site.HostId),
                CertificateNodeIds = existing.CertificateNodeIds.Concat(site.CertificateNodeIds).Distinct().ToArray(),
                FallbackNodeIds = existing.FallbackNodeIds.Concat(site.FallbackNodeIds).Distinct().ToArray()
            };
        }
        var hidden = store.List<string>("hidden-web").ToHashSet();
        return result.Where(t => !automatic.Contains(t.Id) || !hidden.Contains(t.Id)).Select(t =>
        {
            var auto = nodes.Where(n => ConfiguredFallback(n, t)).Select(n => n.Id).Except(t.FallbackNodeIds).ToArray();
            return t with { FallbackNodeIds = t.FallbackNodeIds.Concat(auto).Distinct().ToArray(), AutoFallbackNodeIds = auto };
        }).ToArray();
    }
    public static async Task<Observation[]> Run(WebTarget t, bool includeCertificate, CancellationToken token)
    {
        List<Observation> results = [];
        if (Domain(t).Length > 0) results.Add(await DnsProbe(t, token));
        if (t.CheckWebsite && Domain(t).Length > 0)
        {
            var host = Domain(t);
            var http = HttpProbe(t, "web80", new UriBuilder("http", host, 80).Uri, false, token);
            var https = HttpProbe(t, "web443", new UriBuilder("https", host, 443).Uri, false, token);
            results.AddRange(await Task.WhenAll(http, https));
        }
        if (t.Url.Length > 0) results.Add(await HttpProbe(t, "web", new Uri(t.Url), true, token));
        if (includeCertificate && Address(t).Length > 0)
            results.Add(await Tls("tls", t.Id, Address(t), t.Port, Name(t), t.Location, token));
        return [.. results];
    }

    private static async Task<Observation> DnsProbe(WebTarget t, CancellationToken token)
    {
        var name = Domain(t);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(7));
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(name, timeout.Token);
            var records = addresses.Select(a =>
                $"{(a.AddressFamily == AddressFamily.InterNetwork ? "A" : "AAAA")} {a}").ToList();
            if (!IPAddress.TryParse(name, out _))
            {
                try
                {
                    var aliases = await DnsRecords.QueryAsync(name, QueryType.CNAME, cancellationToken: timeout.Token);
                    records.AddRange(aliases.Answers.OfType<CNameRecord>().Select(x => $"CNAME {x.CanonicalName}"));
                }
                catch (Exception error) when (!token.IsCancellationRequested &&
                    (error is DnsResponseException or SocketException or OperationCanceledException))
                {
                    records.Add("CNAME 查询未完成");
                }
            }
            var detail = string.Join(" · ", records);
            return new("dns", t.Id, DateTimeOffset.Now, addresses.Length > 0 ? "解析正常" : "没有记录", detail);
        }
        catch (SocketException) { return new("dns", t.Id, DateTimeOffset.Now, "DNS 失败", "系统解析器未返回地址。"); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new("dns", t.Id, DateTimeOffset.Now, "超时", "DNS 解析超时。"); }
    }

    private static async Task<Observation> HttpProbe(WebTarget t, string kind, Uri url, bool useRules, CancellationToken token)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = true, MaxAutomaticRedirections = 5 };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
        try
        {
            var sw = Stopwatch.StartNew();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("VPSnode/0.1");
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            var code = (int)response.StatusCode;
            var final = response.RequestMessage?.RequestUri ?? url;
            var detail = $"HTTP {code}" + (final != url ? " · 跳转至 " + final.GetLeftPart(UriPartial.Path) : "");
            var expected = useRules && t.ExpectedStatus.HasValue ? code == t.ExpectedStatus : response.IsSuccessStatusCode;
            string body = "", title = "";
            if (expected)
            {
                using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
                limit.CancelAfter(TimeSpan.FromSeconds(8));
                var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
                Encoding encoding = Encoding.UTF8;
                if (charset is { Length: > 0 })
                {
                    try { encoding = Encoding.GetEncoding(charset); }
                    catch (ArgumentException) { }
                }
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(limit.Token), encoding);
                var chars = new char[65536];
                var count = await reader.ReadBlockAsync(chars.AsMemory(), limit.Token);
                body = new string(chars, 0, count);
                var titleMatch = Regex.Match(body, @"<title\b[^>]*>([\s\S]*?)</title\s*>",
                    RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                if (titleMatch.Success)
                {
                    title = Regex.Replace(WebUtility.HtmlDecode(titleMatch.Groups[1].Value), @"\s+", " ").Trim();
                    if (title.Length > 120) title = title[..120];
                }
            }
            var html = response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml" ||
                Regex.IsMatch(body, @"<(?:!doctype\s+html|html|head|body)\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
            var state = "正常";
            if (!expected) state = "响应异常";
            else if (!useRules && (!html || string.IsNullOrWhiteSpace(body)))
            {
                state = "无网页";
                detail += " · 返回为空或内容非 HTML";
            }
            else if (!useRules && IsPlaceholder(title, body))
            {
                state = "疑似默认页";
                detail += " · 内容为服务器默认页、停放页或验证页";
            }
            else if (useRules && t.Contains.Length > 0 && !body.Contains(t.Contains, StringComparison.Ordinal))
            {
                state = "内容不匹配";
                detail += " · 找不到预期文字";
            }
            else if (!useRules && final != url) state = "跳转后有网页";
            else if (!useRules) state = "有网页";
            if (!useRules && title.Length == 0 && state is "有网页" or "跳转后有网页") detail += " · 未声明页面标题";
            return new(kind, t.Id, DateTimeOffset.Now, state, detail, sw.Elapsed.TotalMilliseconds, url.GetLeftPart(UriPartial.Path),
                Title: title, HttpStatus: code, FinalUrl: final.GetLeftPart(UriPartial.Path));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return new(kind, t.Id, DateTimeOffset.Now, "超时", "HTTP 请求超时。"); }
        catch (HttpRequestException e)
        {
            return new(kind, t.Id, DateTimeOffset.Now, "访问失败",
                e.HttpRequestError == HttpRequestError.SecureConnectionError ? "HTTPS 证书或握手异常。" : "无法完成 HTTP 请求。");
        }
    }

    private static bool IsPlaceholder(string title, string body)
    {
        string[] titles = ["Welcome to nginx!", "Apache2 Ubuntu Default Page", "Apache2 Debian Default Page",
            "IIS Windows Server", "Just a moment...", "Index of /", "Domain parked", "Default Web Site Page"];
        return titles.Any(x => title.Equals(x, StringComparison.OrdinalIgnoreCase)) ||
            body.Contains("If you see this page, the nginx web server", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("This is the default welcome page used to test", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<Observation> Tls(string kind, string id, string address, int port, string sni, string context,
        CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(9));
        Observation Answer(string state, string detail, DateTimeOffset? expiration = null) =>
            new(kind, id, DateTimeOffset.Now, state, detail, Context: context,
                Address: address, Expires: expiration);
        X509Certificate2? cert = null;
        SslPolicyErrors policy = SslPolicyErrors.RemoteCertificateNotAvailable;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, port, timeout.Token);
            using var stream = new SslStream(client.GetStream(), false, (_, presented, _, errors) =>
            {
                policy = errors;
                if (presented is not null) cert = new X509Certificate2(presented);
                return true; // This socket only inspects the presented certificate.
            });
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = sni, ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11]
            }, timeout.Token);
            if (cert is null) return Answer("TLS 无证书", "目标未提供证书。");
            DateTimeOffset expires = cert.NotAfter;
            var detail = $"SNI {sni} · {stream.SslProtocol} · 颁发者 {cert.Issuer} · 生效 {cert.NotBefore:yyyy-MM-dd} · {policy}";
            if (expires <= DateTimeOffset.Now) return Answer("已过期", detail, expires);
            if (policy != SslPolicyErrors.None) return Answer("证书校验失败", detail, expires);
            return Answer(expires <= DateTimeOffset.Now.AddDays(30) ? "即将到期" : "TLS 正常", detail, expires);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return Answer("超时", "TCP 或 TLS 握手超时。"); }
        catch (SocketException e) { return Answer(e.SocketErrorCode == SocketError.HostNotFound ? "DNS 失败" : "连接失败", "无法连接配置端点。"); }
        catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or IOException)
        { return Answer("TLS 失败", "无法完成 TLS 握手。"); }
        finally { cert?.Dispose(); }
    }
}
