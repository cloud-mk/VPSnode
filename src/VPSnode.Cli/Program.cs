using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VPSnode.Core;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = new UTF8Encoding(false);
if (SshPassword.TryAnswer(args) is int answer) return answer;

try
{
    var input = new Arguments(args);
    var words = input.Words;
    if (words.Length == 0 || words is ["help"] || input.Flag("help"))
    {
        Print(new { name = "VPSnode CLI", appVersion = Version(), commands = new[] {
            "version", "validate | import --preview|--apply       stdin: InventoryDocument v1",
            "status [ID]", "asset list <host|node|web>", "asset get <kind> ID",
            "asset add|update <kind> ID [--revision HASH]     stdin: explicit JSON fields",
            "asset delete <kind> ID", "node pause|resume|config|export ID [--include-secrets]",
            "node import-links [--preview]                  stdin: {links:[...],hostId:\"\"}",
            "node duplicates | node associate KEEP_ID REMOVE_ID",
            "ssh aliases | ssh import ALIAS", "v2rayn info|groups|cores | v2rayn sync [--groups ID,ID]",
            "settings get | settings set                    stdin: explicit JSON fields",
            "settings read-v2rayn-target [--apply]", "history KIND ID [--since ISO8601] [--limit 100]",
            "check all|nodes|speeds|websites [--wait] [--timeout 60]",
            "check host|inventory|host-nodes|node|speed|sni|web|tls ID [--wait] [--timeout 60]",
            "task list [--limit 100] | task get|cancel ID | task wait ID [--timeout 60]",
            "worker                                        drain pending work, then exit",
            "data info", "backup export|preview PATH                    stdin: {password:...}",
            "backup restore PATH --mode merge|replace       stdin: {password:...}" },
            contract = "JSON protocolVersion=1. VPSNODE_DATA selects the shared local database. check starts a local worker even without GUI; queued/running is never success. Secrets use stdin; output is redacted unless config/export explicitly opts in.",
            exitCodes = new { success = 0, localError = 1, invalidArguments = 2, notFound = 3, conflict = 4, inspectionFailed = 5, canceled = 6, waitTimeout = 7 } });
        return 0;
    }
    if (words is ["version"]) { input.Allow(); Print(new { appVersion = Version() }); return 0; }
    var store = new Store(); var view = new AgentView(store); var monitor = new VPSnode.Core.Monitor(store);
    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
    void Safe<T>(T value) => Print(view.Safe(value));

    if (words is ["worker"])
    { input.Allow("quiet"); await monitor.Drain(cancel.Token); if (!input.Flag("quiet")) Safe(new { drained = true }); return 0; }
    if (words is ["validate"] or ["import"])
    {
        input.Allow(words[0] == "validate" ? [] : ["preview", "apply"]);
        var apply = words[0] == "import" && input.Flag("apply");
        if (words[0] == "import" && input.Flag("preview") == apply) throw Usage("import 需要且仅接受 --preview 或 --apply。");
        var document = Wire.Decode<InventoryDocument>(await ReadInput());
        var summary = await monitor.Maintain(() => store.Import(document, apply), cancel.Token);
        if (apply) StartWorker(store);
        Safe(summary); return summary.Conflicts.Length > 0 ? 4 : 0;
    }
    if (words is ["status"] or ["status", _])
    {
        input.Allow(); var id = words.ElementAtOrDefault(1);
        bool Show(string current) => id is null || id == current;
        var hosts = store.Hosts.Where(h => Show(h.Id)).ToArray();
        var nodes = store.Nodes.Where(n => Show(n.Id)).ToArray();
        var websites = WebProbe.Targets(store).Where(w => Show(w.Id)).ToArray();
        if (id != null && hosts.Length + nodes.Length + websites.Length == 0) throw new KeyNotFoundException("未找到资产。");
        Safe(new { hosts = hosts.Select(view.Host), nodes = nodes.Select(n => view.Node(n)), websites = websites.Select(AgentView.Web),
            observations = store.Latest().Values.Where(o => Show(o.Id)).Select(AgentView.Observation), tasks = store.Tasks.Where(t => id == null || t.TargetId == id).Take(100), settings = store.Settings });
        return 0;
    }
    if (words is ["asset", var action, var kind, ..])
    {
        if (kind is not ("host" or "node" or "web")) throw Usage("资产类型应为 host、node 或 web。");
        var id = words.ElementAtOrDefault(3);
        object[] Assets() => kind switch { "host" => store.Hosts.Select(view.Host).ToArray(), "node" => store.Nodes.Select(n => view.Node(n)).ToArray(), _ => WebProbe.Targets(store).Select(AgentView.Web).ToArray() };
        if (action == "list" && words.Length == 3) { input.Allow(); Safe(new { items = Assets() }); return 0; }
        if (id == null || words.Length != 4) throw Usage("需要资产 ID。");
        object Find() => kind switch {
            "host" => view.Host(store.Hosts.FirstOrDefault(h => h.Id == id) ?? throw Missing()),
            "node" => view.Node(store.Nodes.FirstOrDefault(n => n.Id == id) ?? throw Missing()),
            _ => AgentView.Web(WebProbe.Targets(store).FirstOrDefault(w => w.Id == id) ?? throw Missing()) };
        if (action == "get") { input.Allow(); Safe(Find()); return 0; }
        if (action is "add" or "update")
        {
            input.Allow("revision"); using var patch = JsonDocument.Parse(await ReadInput());
            await monitor.Maintain(() => store.EditAsset(kind, id, patch.RootElement, action == "add", input.Value("revision")), cancel.Token);
            Safe(Find()); return 0;
        }
        if (action == "delete")
        { input.Allow(); _ = Find(); await monitor.Delete(kind, id, cancel.Token); Safe(new { deleted = true, kind, id }); return 0; }
    }
    if (words is ["node", "duplicates"])
    { input.Allow(); Safe(new { items = store.FindDuplicateCandidates() }); return 0; }
    if (words is ["node", "associate", var keep, var remove])
    {
        input.Allow();
        if (!store.Nodes.Any(n => n.Id == keep) || !store.Nodes.Any(n => n.Id == remove)) throw Missing();
        await monitor.Maintain(() => { store.MergeNodes(keep, remove); return true; }, cancel.Token);
        Safe(new { associated = true, keepId = keep, removedId = remove }); return 0;
    }
    if (words is ["node", "import-links"])
    {
        input.Allow("preview"); var links = Wire.Decode<LinksInput>(await ReadInput());
        if (links.Links is null || links.Links.Length == 0) throw Usage("links 不能为空。");
        var document = new InventoryDocument(1, [], links.Links.Select(l => ShareLink.New(l, links.HostId)).ToArray(), []);
        var summary = await monitor.Maintain(() => store.Import(document, !input.Flag("preview")), cancel.Token);
        if (!input.Flag("preview")) StartWorker(store);
        Safe(summary); return summary.Conflicts.Length > 0 ? 4 : 0;
    }
    if (words is ["node", var nodeAction, var nodeId])
    {
        input.Allow("include-secrets"); var node = store.Nodes.FirstOrDefault(n => n.Id == nodeId) ?? throw Missing();
        if (nodeAction is "pause" or "resume")
        {
            input.Allow();
            using var patch = JsonDocument.Parse(nodeAction == "pause" ? "{\"paused\":true}" : "{\"paused\":false,\"sourceRemoved\":false,\"manual\":true}");
            await monitor.Maintain(() => store.EditAsset("node", nodeId, patch.RootElement), cancel.Token);
            Safe(view.Node(store.Nodes.Single(n => n.Id == nodeId))); return 0;
        }
        if (nodeAction == "config")
        { var data = view.Node(node, input.Flag("include-secrets")); if (input.Flag("include-secrets")) Print(data); else Safe(data); return 0; }
        if (nodeAction == "export")
        {
            if (!input.Flag("include-secrets")) throw Usage("导出完整配置需要 --include-secrets；请将输出保存在受保护的本地文件。");
            Host[] hosts = store.Hosts.Where(h => h.Id == node.HostId).Select(h => h with { PasswordCipher = "" }).ToArray();
            Print(new { inventory = new InventoryDocument(1, hosts, [node with { Link = ProxyProbe.ClientLink(store, node) }], []) }); return 0;
        }
    }
    if (words is ["ssh", "aliases"])
    { input.Allow(); Safe(new { items = SshInventory.KnownAliases(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config")) }); return 0; }
    if (words is ["ssh", "import", var alias])
    {
        input.Allow(); var host = await SshInventory.FromAlias(alias, cancel.Token);
        var result = await monitor.Maintain(() => store.ImportSshAlias(host), cancel.Token);
        host = result.Host;
        if (result.Summary.Conflicts.Length > 0) { Safe(result.Summary); return 4; }
        var task = store.SubmitTask("inventory", host.Id); StartWorker(store); Safe(new { assetId = host.Id, task }); return 0;
    }
    if (words is ["v2rayn", "info"])
    {
        input.Allow(); var directory = store.Settings.V2Directory;
        Safe(new { directory, groups = V2Sync.Groups(directory), xray = ProxyProbe.FindCore(directory, "xray"), singBox = ProxyProbe.FindCore(directory, "sing-box"), pingUrl = V2Sync.PingTarget(directory) }); return 0;
    }
    if (words is ["v2rayn", "groups"])
    { input.Allow(); Safe(new { items = V2Sync.Groups(store.Settings.V2Directory) }); return 0; }
    if (words is ["v2rayn", "cores"])
    { input.Allow(); var directory = store.Settings.V2Directory; Safe(new { directory, xray = ProxyProbe.FindCore(directory, "xray"), singBox = ProxyProbe.FindCore(directory, "sing-box") }); return 0; }
    if (words is ["v2rayn", "sync"])
    {
        input.Allow("groups"); var groups = input.Value("groups")?.Split(',').Select(g => g == "@ungrouped" ? "" : g).Distinct().ToArray() ?? V2Sync.Groups(store.Settings.V2Directory).Select(g => g.Id).ToArray();
        var known = V2Sync.Groups(store.Settings.V2Directory).Select(g => g.Id).ToHashSet();
        if (groups.Length == 0 || groups.Any(g => !known.Contains(g))) throw Usage("同步分组为空或不存在。");
        var summary = await monitor.Maintain(() => monitor.Sync(false, groups), cancel.Token); Safe(summary); return summary.Conflicts.Length > 0 ? 4 : 0;
    }
    if (words is ["settings", "get"]) { input.Allow(); Safe(store.Settings); return 0; }
    if (words is ["settings", "set"])
    {
        input.Allow(); using var patch = JsonDocument.Parse(await ReadInput());
        Safe(store.EditSettings(patch.RootElement)); return 0;
    }
    if (words is ["settings", "read-v2rayn-target"])
    {
        input.Allow("apply"); var url = V2Sync.PingTarget(store.Settings.V2Directory);
        if (input.Flag("apply")) { using var patch = JsonDocument.Parse(Wire.Encode(new { pingUrl = url })); store.EditSettings(patch.RootElement); }
        Safe(new { pingUrl = url, applied = input.Flag("apply") }); return 0;
    }
    if (words is ["history", var historyKind, var historyId])
    {
        input.Allow("limit", "since"); var since = DateTimeOffset.MinValue;
        if (input.Value("since") is string sinceText && !DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out since)) throw Usage("since 应为 ISO8601 时间。");
        Safe(new { items = store.History(historyKind, historyId, since, input.Number("limit", 100, 1, 5000)).Select(AgentView.Observation) }); return 0;
    }
    if (words is ["check", var checkKind, ..] && words.Length <= 3)
    {
        input.Allow("wait", "timeout"); var timeout = input.Number("timeout", 60, 1, 86400);
        var task = store.SubmitTask(checkKind, words.ElementAtOrDefault(2) ?? "");
        try { StartWorker(store); }
        catch { store.CancelTask(task.Id); throw new IOException("无法启动本地执行进程，任务已取消。请检查 CLI 安装。"); }
        if (input.Flag("wait")) return await WaitTask(task.Id, timeout);
        Safe(new { task = store.GetTask(task.Id), executor = "local-worker", accepted = true }); return 0;
    }
    if (words is ["task", "list"])
    { input.Allow("limit"); Safe(new { items = store.Tasks.Take(input.Number("limit", 100, 1, 5000)) }); return 0; }
    if (words is ["task", var taskAction, var taskId])
    {
        if (taskAction == "get") { input.Allow(); Safe(store.GetTask(taskId) ?? throw Missing()); return 0; }
        if (taskAction == "cancel") { input.Allow(); Safe(store.CancelTask(taskId)); return 0; }
        if (taskAction == "wait")
        { input.Allow("timeout"); var seconds = input.Number("timeout", 60, 1, 86400); _ = store.GetTask(taskId) ?? throw Missing(); StartWorker(store); return await WaitTask(taskId, seconds); }
    }
    if (words is ["data", "info"])
    { input.Allow(); Safe(new { store.DirectoryPath, hosts = store.Hosts.Length, nodes = store.Nodes.Length, websites = WebProbe.Targets(store).Length, tasks = store.Tasks.Length }); return 0; }
    if (words is ["backup", var backupAction, var path] && backupAction is "export" or "preview" or "restore")
    {
        input.Allow(backupAction == "restore" ? ["mode"] : []);
        var credential = Wire.Decode<BackupInput>(await ReadInput());
        if (credential.Password is null) throw Usage("需要备份口令。");
        if (backupAction == "export")
        { await monitor.Maintain(() => { store.ExportBackup(path, credential.Password); return true; }, cancel.Token); Safe(new { saved = true, path = Path.GetFullPath(path) }); return 0; }
        if (backupAction == "preview") { Safe(store.PreviewBackup(path, credential.Password)); return 0; }
        var mode = input.Value("mode"); if (mode is not ("merge" or "replace")) throw Usage("恢复需要 --mode merge 或 replace。");
        Safe(await monitor.Maintain(() => store.ImportBackup(path, credential.Password, mode == "replace"), cancel.Token)); return 0;
    }
    throw Usage("未知命令或参数组合。请运行 help。");

    async Task<int> WaitTask(string id, int seconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (true)
        {
            var task = store.GetTask(id) ?? throw Missing();
            if (task.Terminal) { Safe(task); return task.State == "completed" ? 0 : task.State == "canceled" ? 6 : 5; }
            if (DateTimeOffset.UtcNow >= deadline) { Safe(new { task, error = new { code = "wait_timeout", message = "等待超时；任务仍可查询或取消。" } }); return 7; }
            await Task.Delay(100, cancel.Token);
        }
    }
}
catch (Exception error)
{
    var (code, message, status) = error switch {
        ArgumentException => ("invalid_arguments", error.Message, 2),
        JsonException => ("invalid_json", "JSON 格式、字段类型或必填字段错误。", 2),
        InvalidDataException => ("validation_failed", error.Message, 2),
        KeyNotFoundException => ("not_found", error.Message, 3),
        InvalidOperationException => ("conflict", error.Message, 4),
        OperationCanceledException => ("canceled", "当前操作已取消；已提交任务可用 task get 查询。", 6),
        IOException => ("io_error", "本地文件、依赖或执行进程不可用。", 1),
        _ => ("local_error", "读取或写入本地数据失败。", 1) };
    Print(new { error = new { code, message } }); return status;
}

static string Version() => typeof(Store).Assembly.GetName().Version!.ToString(3);
static ArgumentException Usage(string message) => new(message);
static KeyNotFoundException Missing() => new("未找到指定对象。");
static void Print<T>(T value)
{
    var json = JsonSerializer.SerializeToNode(value, Wire.Json) as JsonObject ?? new JsonObject { ["data"] = JsonSerializer.SerializeToNode(value, Wire.Json) };
    json["protocolVersion"] = 1; Console.WriteLine(json.ToJsonString(Wire.Json));
}
static void StartWorker(Store store)
{
    var executable = Environment.ProcessPath ?? throw new IOException("CLI 路径不可用。");
    // ShellExecute starts without inheriting the submitting Agent's redirected pipe
    // handles. Otherwise ReadToEnd can wait for a supposedly detached worker to exit.
    Environment.SetEnvironmentVariable("VPSNODE_DATA", store.DirectoryPath);
    var info = new ProcessStartInfo(executable) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
        WorkingDirectory = store.DirectoryPath };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("worker"); info.ArgumentList.Add("--quiet");
    using var process = Process.Start(info) ?? throw new IOException("无法启动执行进程。");
}
static async Task<string> ReadInput()
{
    const int maximum = 4 * 1024 * 1024;
    using var content = new StringWriter(); var buffer = new char[8192];
    while (true)
    {
        var count = await Console.In.ReadAsync(buffer); if (count == 0) return content.ToString().TrimStart('\uFEFF');
        if (content.GetStringBuilder().Length + count > maximum) throw new InvalidDataException("输入 JSON 过大。");
        content.Write(buffer, 0, count);
    }
}
sealed record LinksInput(string[] Links, string HostId = "");
sealed record BackupInput(string Password);
sealed class Arguments
{
    private readonly Dictionary<string, string?> _options = [];
    public string[] Words { get; }
    public Arguments(string[] arguments)
    {
        List<string> words = [];
        for (int i = 0; i < arguments.Length; i++)
        {
            var item = arguments[i];
            if (item == "--help") { _options.TryAdd("help", null); continue; }
            if (!item.StartsWith("--")) { if (item.StartsWith('-')) throw new ArgumentException("无效选项。"); words.Add(item); continue; }
            var name = item[2..];
            string? value = null;
            if (name is "timeout" or "limit" or "since" or "revision" or "groups" or "mode")
            { if (++i >= arguments.Length || arguments[i].StartsWith("--")) throw new ArgumentException("选项缺少值。"); value = arguments[i]; }
            if (!_options.TryAdd(name, value)) throw new ArgumentException("重复选项。");
        }
        Words = [.. words];
    }
    public bool Flag(string name) => _options.ContainsKey(name);
    public string? Value(string name) => _options.GetValueOrDefault(name);
    public int Number(string name, int fallback, int min, int max) => Value(name) is not string text ? fallback :
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) && result >= min && result <= max ? result : throw new ArgumentException("数值选项超出允许范围。");
    public void Allow(params string[] options) { if (_options.Keys.Except(options).Any()) throw new ArgumentException("命令包含不支持的选项。"); }
}
