using System.Net.NetworkInformation;

namespace VPSnode.Core;

public sealed class Monitor
{
    private readonly Store _store;
    private readonly SemaphoreSlim _gate = new(1);
    private DateTimeOffset _lastTick = DateTimeOffset.MinValue;
    private DateTimeOffset _lastCleanup = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSync = DateTimeOffset.MinValue;
    private bool _offline;

    public Monitor(Store store) => _store = store;

    // Maintenance shares the same gate as manual, queued and scheduled probes.
    public async Task<T> Maintain<T>(Func<T> action, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { token.ThrowIfCancellationRequested(); return action(); }
        finally { _gate.Release(); }
    }

    public async Task Delete(string kind, string id, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try { _store.Delete(kind, id); }
        finally { _gate.Release(); }
    }

    public async Task<string> Run(string kind, string targetId, CancellationToken token, Action? started = null)
    {
        try { await _gate.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            var skipped = Plan(kind, targetId).Select(w => new InspectionItem(w.Kind, w.Id, w.Name, "跳过", "等待执行期间已取消")).ToArray();
            var canceled = new Inspection(kind, "手动", DateTimeOffset.Now, "已取消", skipped);
            _store.Record(new("task", "history", DateTimeOffset.Now, canceled.State, Wire.Encode(canceled)));
            throw;
        }
        try { started?.Invoke(); return await Execute(kind, targetId, token); }
        finally { _gate.Release(); }
    }

    public async Task Tick(CancellationToken token)
    {
        if (!await _gate.WaitAsync(0, token)) return;
        try
        {
            var now = DateTimeOffset.Now;
            var settings = _store.Settings;
            if (now - _lastCleanup >= TimeSpan.FromHours(24))
            {
                _store.Trim(settings.HistoryDays);
                _lastCleanup = now;
            }
            if (now - _lastSync >= TimeSpan.FromMinutes(2) && settings.FollowGroups.Length > 0)
            {
                _lastSync = now;
                Sync(true, settings.FollowGroups);
            }
            if (!NetworkInterface.GetIsNetworkAvailable())
            {
                if (!_offline) _store.Record(new("scheduler", "local", now, "本机无网络", "暂停本机网络巡检。"));
                _offline = true;
                return;
            }
            for (int i = 0; i < 12 && _store.Peek() is Request request; i++)
            {
                await Execute(request.Kind, request.TargetId, token, "任务队列");
                _store.Finish(request);
            }
            // Reload after queued work: settings may have changed while a probe was running.
            settings = _store.Settings;
            if (!settings.ScheduledInspectionEnabled) { _offline = false; return; }
            if (!_offline && now - _lastTick < TimeSpan.FromMinutes(Math.Clamp(settings.IntervalMinutes, 2, 1440))) return;
            _offline = false;
            _lastTick = now;
            await Execute("scheduled", "", token, "定时");
        }
        finally { _gate.Release(); }
    }

    public SyncSummary Sync(bool auto, string[] groups)
    {
        try
        {
            var result = V2Sync.Apply(_store, _store.Settings.V2Directory, groups, auto);
            _store.Record(new("sync", "v2rayN", DateTimeOffset.Now, "已同步",
                $"新增 {result.Added} · 更新 {result.Updated} · 自动暂停 {result.Paused} · 不支持 {result.Unsupported} · 冲突 {result.Conflicts.Length}"));
            return result;
        }
        catch (Exception e) when (e is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            _store.Record(new("sync", "v2rayN", DateTimeOffset.Now, "同步失败", "v2rayN 数据不可读，已保留现有节点和巡检状态。"));
            if (!auto) throw new InvalidDataException("v2rayN 数据不可读，已保留现有节点和巡检状态。");
            return new(0, 0, 0, 0, ["v2rayN 数据不可读。"]);
        }
    }

    private readonly object _stateLock = new();
    private CancellationTokenSource? _active;
    public event Action<Inspection>? Changed;

    public void CancelActive()
    {
        lock (_stateLock) _active?.Cancel();
    }

    private async Task<string> Execute(string kind, string id, CancellationToken outer, string source = "手动")
    {
        var plan = Plan(kind, id).ToArray();
        if (plan.Length == 0 && kind is "node" or "speed" or "sni" or "host" or "inventory" or "web")
            plan = [new Work(kind, id, id)];
        var items = plan.Select(x => new InspectionItem(x.Kind, x.Id, x.Name, "排队", "")).ToArray();
        var started = DateTimeOffset.Now;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(outer);
        lock (_stateLock) _active = cancel;
        Inspection Snapshot(string state) => new(kind, source, started, state, [.. items]);
        try
        {
            Changed?.Invoke(Snapshot("运行中"));
            for (var i = 0; i < items.Length; i++)
            {
                cancel.Token.ThrowIfCancellationRequested();
                var item = items[i];
                items[i] = item with { State = "测试中" };
                Changed?.Invoke(Snapshot("运行中"));
                try
                {
                    var result = await Inspect(plan[i], cancel.Token);
                    items[i] = item with { State = result.State, Detail = result.Detail };
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    items[i] = item with { State = "失败", Detail = $"检测未完成（{ex.GetType().Name}）" };
                }
                Changed?.Invoke(Snapshot("运行中"));
            }
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            for (var i = 0; i < items.Length; i++)
                if (items[i].State is "排队" or "测试中")
                    items[i] = items[i] with { State = "跳过", Detail = items[i].State == "排队" ? "任务取消，尚未开始" : "执行中取消" };
        }
        finally { lock (_stateLock) _active = null; }
        var final = Snapshot(cancel.IsCancellationRequested ? "已取消" : "已结束");
        _store.Record(new("task", "history", DateTimeOffset.Now, final.State, Wire.Encode(final)));
        Changed?.Invoke(final);
        return items.Length == 1 ? $"{items[0].State} · {items[0].Detail}" : final.Summary;
    }

    private sealed record Work(string Kind, string Id, string Name, Host? Host = null, Node? Node = null, WebTarget? Web = null, bool Certificate = true);

    private IEnumerable<Work> Plan(string kind, string id)
    {
        bool sweep = kind is "all" or "scheduled";
        if (sweep || kind is "host" or "inventory")
            foreach (var h in _store.Hosts.Where(h => sweep ? h.Enabled : h.Id == id))
                yield return new(kind == "inventory" ? "inventory" : "host", h.Id, h.Name, Host: h);
        if (sweep || kind is "node" or "speed" or "speeds" or "sni" or "nodes" or "host-nodes")
            foreach (var n in _store.Nodes.Where(n => sweep || kind is "nodes" or "speeds" || (kind == "host-nodes" ? n.HostId == id : n.Id == id)))
                yield return new(kind == "speeds" ? "speed" : kind is "speed" or "sni" ? kind : "node", n.Id, n.Name, Node: n);
        if (sweep || kind is "websites" or "web")
        {
            var latest = _store.Latest();
            foreach (var w in WebProbe.Targets(_store).Where(w => kind == "web" ? w.Id == id : w.Enabled))
            {
                var cert = latest.GetValueOrDefault(("tls", w.Id));
                yield return new("web", w.Id, w.Name, Web: w, Certificate: kind != "scheduled" || cert is null || DateTimeOffset.Now - cert.At >= TimeSpan.FromDays(1));
            }
        }
    }

    private async Task<(string State, string Detail)> Inspect(Work work, CancellationToken token)
    {
        Observation[] results;
        if (work.Host is Host host)
            results = [await SshInventory.Probe(_store, host, work.Kind == "inventory", token)];
        else if (work.Web is WebTarget web)
            results = await WebProbe.Run(web, work.Certificate, token);
        else if (work.Node is Node node)
        {
            if (node.Paused || node.SourceRemoved && !node.VpsVerified && !node.Manual)
                return ("跳过", node.Paused ? "已暂停" : "唯一的 v2rayN 来源已删除");
            if (node.Server.GetValueOrDefault("启用") == "否" || node.Server.GetValueOrDefault("客户端启用") == "否")
                return ("跳过", "面板入站或客户端已禁用");
            if (work.Kind == "sni")
            {
                string client;
                try { client = ProxyProbe.ClientLink(_store, node); }
                catch (InvalidDataException ex) { return ("跳过", ex.Message); }
                var link = ShareLink.Parse(client);
                if (link.Security != "reality") return ("跳过", "仅适用于 REALITY");
                results = await Task.WhenAll(WebProbe.Tls("sni-local", node.Id, link.Sni, 443, link.Sni, "Windows 直连", token),
                    SshInventory.CheckTarget(_store.Hosts.FirstOrDefault(h => h.Id == node.HostId), node, link, token),
                    ProxyProbe.Run(_store, node, false, token));
                results[2] = results[2] with { Kind = "sni-node" };
            }
            else results = [await ProxyProbe.Run(_store, node, work.Kind == "speed", token)];
        }
        else return ("跳过", "目标不存在");
        foreach (var result in results) _store.Record(result);
        var issues = results.Where(r => r.State is not ("可用" or "完成" or "正常" or "有网页" or "跳转后有网页" or "SSH 可登录" or "TLS 正常" or "即将到期" or "解析正常")).ToArray();
        if (work.Kind == "inventory" && results.Any(r => r.State == "SSH 可登录" &&
            (r.Detail.Contains("无法读取") || r.Detail.Contains("缺少") || r.Detail.Contains("采集存在问题"))))
            return ("失败", results[0].Detail);
        var state = results.Length == 0 ? "跳过" : issues.Length == 0 ? "成功" :
            issues.All(r => r.State is "待补全" or "参数待验证" or "未检测" or "待配置" or "依赖缺失") ? "跳过" :
            issues.Any(r => r.State.Contains("超时")) ? "超时" : "失败";
        return (state, string.Join(" · ", (issues.Length > 0 ? issues : results).Select(r => $"{r.State} {r.Detail}".Trim())));
    }
}

public sealed record InspectionItem(string Kind, string Id, string Name, string State, string Detail);
public sealed record Inspection(string Kind, string Source, DateTimeOffset Started, string State, InspectionItem[] Items)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int Success => Items.Count(i => i.State == "成功");
    [System.Text.Json.Serialization.JsonIgnore]
    public int Failed => Items.Count(i => i.State is "失败" or "超时");
    [System.Text.Json.Serialization.JsonIgnore]
    public int Skipped => Items.Count(i => i.State == "跳过");
    [System.Text.Json.Serialization.JsonIgnore]
    public int Remaining => Items.Length - Success - Failed - Skipped;
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"共 {Items.Length} · 成功 {Success} · 失败 {Failed} · 跳过 {Skipped} · 剩余 {Remaining}";
    [System.Text.Json.Serialization.JsonIgnore]
    public string Label => Kind switch { "nodes" => "全测延迟", "speeds" => "全测下载速度", "host-nodes" => "分组测延迟", "node" => "节点延迟",
        "inventory" => "VPS 盘点", "host" => "SSH 检查", "speed" => "下载测速", "sni" => "REALITY 诊断", "web" or "websites" => "网站与证书", _ => "综合巡检" };
}
