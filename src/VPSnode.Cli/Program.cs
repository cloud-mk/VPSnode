using System.Text;
using System.Text.Json;
using VPSnode.Core;

Console.InputEncoding = Encoding.UTF8;
Console.OutputEncoding = Encoding.UTF8;

if (SshPassword.TryAnswer(args) is int result) return result;

try
{
    if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
    {
        Console.WriteLine("""
            VPSnode CLI
              version
              validate       从标准输入校验版本 1 JSON
              import --preview | --apply    从标准输入读取版本 1 JSON
              status [ID]    查看脱敏资产与最近检测
              check <all|nodes|speeds|host|inventory|host-nodes|node|web|sni|speed> [ID]  提交本地巡检
            环境变量 VPSNODE_DATA 可选择数据目录。巡检由运行中的 Windows 桌面程序处理。
            """);
        return 0;
    }
    if (args is ["version"])
    {
        Print(new { protocolVersion = 1, appVersion = typeof(Store).Assembly.GetName().Version!.ToString(3) });
        return 0;
    }
    var store = new Store();
    if (args is ["validate"] or ["import", "--preview"] or ["import", "--apply"])
    {
        var content = await ReadInput();
        var input = Wire.Decode<InventoryDocument>(content);
        Print(store.Import(input, apply: args is ["import", "--apply"]));
        return 0;
    }
    if (args[0] == "status" && args.Length <= 2)
    {
        var id = args.ElementAtOrDefault(1);
        bool Show(string current) => id is null || id == current;
        Print(new
        {
            hosts = store.Hosts.Where(x => Show(x.Id)).Select(x => new
            { x.Id, x.Name, x.Hostname, x.Address, x.Ipv6, x.Provider, x.Hardware, x.TunnelPort, x.RemotePanelPort, x.PanelScheme, x.InventoriedAt }),
            nodes = store.Nodes.Where(x => Show(x.Id)).Select(x => new
            { x.Id, x.Name, x.HostId, x.Protocol, x.Transport, x.Security, x.Address, x.Port, x.Paused, x.SourceRemoved }),
            websites = WebProbe.Targets(store).Where(x => Show(x.Id)).Select(x => new
            { x.Id, x.Name, x.HostId, x.Domain, x.Port, x.Location, x.FallbackNodeIds }),
            observations = store.Latest().Values.Where(x => Show(x.Id)).Select(x => new
            { x.Kind, x.Id, x.At, x.State, x.Value, x.Expires, x.Title, x.HttpStatus })
        });
        return 0;
    }
    if (args is ["check", "all" or "nodes" or "speeds"])
    {
        store.Queue(args[1], "");
        Print(new { queued = true, kind = args[1] });
        return 0;
    }
    if (args is ["check", var kind, var target] && Validation.Token(target) &&
        kind is "host" or "inventory" or "host-nodes" or "node" or "speed" or "sni" or "web")
    {
        var exists = kind switch
        {
            "host" or "inventory" or "host-nodes" => store.Hosts.Any(h => h.Id == target),
            "web" => WebProbe.Targets(store).Any(w => w.Id == target),
            _ => store.Nodes.Any(n => n.Id == target)
        };
        if (!exists) throw new InvalidDataException("未找到指定对象。");
        store.Queue(kind, target);
        Print(new { queued = true, kind, targetId = target });
        return 0;
    }
    Print(new { error = "未知命令。请运行 help。" });
    return 2;
}
catch (Exception error)
{
    Print(new
    {
        error = error is InvalidDataException ? error.Message : error is JsonException ? "JSON 格式错误。" : "读取或写入本地数据失败。"
    });
    return 1;
}

static void Print<T>(T value) => Console.WriteLine(Wire.Encode(value));

static async Task<string> ReadInput()
{
    const int maximum = 4 * 1024 * 1024;
    using var content = new StringWriter();
    var buffer = new char[8192];
    while (true)
    {
        var count = await Console.In.ReadAsync(buffer);
        if (count == 0) return content.ToString();
        if (content.GetStringBuilder().Length + count > maximum)
            throw new InvalidDataException("导入 JSON 过大。");
        content.Write(buffer, 0, count);
    }
}
