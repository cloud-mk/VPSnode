using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VPSnode.Core;
using Validation = VPSnode.Core.Validation;
using Forms = System.Windows.Forms;

namespace VPSnode.Desktop;

public partial class MainWindow
{
    private StackPanel SettingsPage()
    {
        var current = _store.Settings;
        TextBox Input(string text, string label, double width = double.NaN)
        {
            var input = new TextBox { Text = text, Width = width, MinWidth = 80,
                HorizontalAlignment = double.IsNaN(width) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left };
            System.Windows.Automation.AutomationProperties.SetName(input, label);
            return input;
        }
        FrameworkElement Field(string label, FrameworkElement input, string help = "")
        {
            var title = Ui.Text(label, brush: "Quiet");
            title.Margin = new Thickness(0, 8, 0, 5);
            var block = Ui.Stack(title, input);
            if (help.Length > 0)
            {
                var hint = Ui.Text(help, 12, "Quiet");
                hint.Margin = new Thickness(0, 5, 0, 3);
                block.Children.Add(hint);
            }
            return block;
        }
        FrameworkElement Unit(TextBox box, string unit)
        {
            var label = Ui.Text(unit, brush: "Quiet");
            label.Margin = new Thickness(8, 0, 0, 0);
            return Ui.Row(box, label);
        }
        var path = Input(current.V2Directory, "v2rayN 安装目录");
        var interval = Input(current.IntervalMinutes.ToString(), "巡检间隔", 88);
        var history = Input(current.HistoryDays.ToString(), "历史保留天数", 88);
        var delay = Input(current.PingUrl, "延迟测试 URL");
        var pingStatus = Input(current.PingStatus.ToString(), "期望 HTTP 状态码", 88);
        var speed = Input(current.SpeedUrl, "下载测速 URL");
        // Decimal MB keeps every byte on a no-op save, including older non-round limits.
        var bytes = Input((current.SpeedBytes / 1_000_000m).ToString("0.######", CultureInfo.CurrentCulture), "单次下载流量上限 MB", 112);
        var enabled = new CheckBox { Content = "启用定时巡检", IsChecked = current.ScheduledInspectionEnabled };
        interval.IsEnabled = enabled.IsChecked == true;
        enabled.Checked += (_, _) => interval.IsEnabled = true;
        enabled.Unchecked += (_, _) => interval.IsEnabled = false;
        var dark = new CheckBox { Content = "深色模式", IsChecked = current.Theme == "dark" };
        void Theme(object sender, RoutedEventArgs args)
        {
            var theme = dark.IsChecked == true ? "dark" : "light";
            _store.Save(_store.Settings with { Theme = theme });
            Ui.Theme(theme);
        }
        dark.Checked += Theme; dark.Unchecked += Theme;
        var feedback = Ui.Text("修改后点击“保存设置”。外观即时生效。", 12, "Quiet");
        var coreHint = Ui.Text("", 12, "Quiet");
        void CheckCore()
        {
            var dir = path.Text.Trim().Trim('"');
            coreHint.Text = $"Xray：{(ProxyProbe.FindCore(dir, "xray") is null ? "未找到" : "已发现")}  ·  sing-box：{(ProxyProbe.FindCore(dir, "sing-box") is null ? "未找到" : "已发现")}";
        }
        bool Save()
        {
            try
            {
                var dir = path.Text.Trim().Trim('"');
                if (dir.Length > 0 && !File.Exists(Path.Combine(dir, "v2rayN.exe")))
                    throw new InvalidDataException("请选择包含 v2rayN.exe 的安装目录。");
                if (!Validation.WebUrl(delay.Text.Trim()) || !Validation.WebUrl(speed.Text.Trim()))
                    throw new InvalidDataException("请填写 http 或 https 测试地址。");
                if (!decimal.TryParse(bytes.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var mb) ||
                    mb < .1m || mb > 100m || mb * 1_000_000m != decimal.Truncate(mb * 1_000_000m))
                    throw new InvalidDataException("下载上限应为 0.1–100 MB，最多六位小数。");
                var old = _store.Settings;
                var updated = old with
                {
                    V2Directory = dir, FollowGroups = string.Equals(dir, old.V2Directory, StringComparison.OrdinalIgnoreCase) ? old.FollowGroups : [],
                    ScheduledInspectionEnabled = enabled.IsChecked == true,
                    IntervalMinutes = Integer(interval.Text, 2, 1440, "巡检间隔"),
                    HistoryDays = Integer(history.Text, 1, 365, "历史保留天数"),
                    PingUrl = delay.Text.Trim(), PingStatus = Integer(pingStatus.Text, 100, 599, "延迟测试状态码"),
                    SpeedUrl = speed.Text.Trim(), SpeedBytes = checked((int)(mb * 1_000_000m))
                };
                _store.Save(updated);
                UpdateTaskPanel();
                feedback.Text = "设置已保存";
                Status("设置已保存");
                CheckCore();
                return true;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }
        var browse = Ui.Button("浏览…", () =>
        {
            using var dialog = new Forms.FolderBrowserDialog();
            if (Directory.Exists(path.Text)) dialog.SelectedPath = path.Text;
            if (dialog.ShowDialog() == Forms.DialogResult.OK) path.Text = dialog.SelectedPath;
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right); pathRow.Children.Add(browse); pathRow.Children.Add(path);
        var readTarget = Ui.Button("读取 v2rayN 测试目标", () =>
        {
            try
            {
                using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(path.Text.Trim(), "guiConfigs", "guiNConfig.json")));
                var item = config.RootElement.EnumerateObject().First(p => p.Name.Equals("SpeedTestItem", StringComparison.OrdinalIgnoreCase)).Value;
                var url = item.EnumerateObject().First(p => p.Name.Equals("SpeedPingTestUrl", StringComparison.OrdinalIgnoreCase)).Value.GetString();
                if (url is null || !Validation.WebUrl(url)) throw new InvalidDataException("v2rayN 测试目标无效。");
                delay.Text = url;
                feedback.Text = "测试目标已读取；保存设置后生效。";
            }
            catch (Exception ex) { Fail(ex); }
        });
        readTarget.HorizontalAlignment = HorizontalAlignment.Left;
        readTarget.Margin = new Thickness(0, 8, 0, 6);
        var body = Ui.Stack(
            Ui.Card(Ui.Stack(Ui.Section("外观"), dark, Ui.Text("主题即时保存，其他输入保持不变。", 12, "Quiet")), 14),
            Ui.Card(Ui.Stack(Ui.Section("v2rayN 集成"), Field("安装目录", pathRow),
                Ui.Row(Ui.Button("检查内核", CheckCore), Ui.Button("查看分组和同步", () =>
                {
                    if (!string.Equals(path.Text.Trim().Trim('"'), _store.Settings.V2Directory, StringComparison.OrdinalIgnoreCase))
                    { feedback.Text = "安装目录已修改，请先保存设置再同步。"; return; }
                    SyncDialog();
                })), coreHint), 14),
            Ui.Card(Ui.Stack(Ui.Section("定时巡检"), enabled,
                Field("巡检间隔", Unit(interval, "分钟"), "2–1440 分钟。关闭后不再启动周期巡检；当前任务继续，手动检测和队列仍可用。")), 14),
            Ui.Card(Ui.Stack(Ui.Section("延迟测试"), Field("测试 URL", delay),
                Field("期望 HTTP 状态码", Unit(pingStatus, "例如 204")),
                readTarget, Ui.Text("经过当前节点发起真实代理请求；预热后计时一次。", 12, "Quiet")), 14),
            Ui.Card(Ui.Stack(Ui.Section("下载测速"), Field("下载地址", speed),
                Field("单次下载流量上限", Unit(bytes, "MB"), "1 MB = 1,000,000 字节；范围 0.1–100 MB。仅手动测速使用，每节点最多 45 秒。")), 14),
            Ui.Card(Ui.Stack(Ui.Section("数据管理"), Field("历史保留", Unit(history, "天"), "1–365 天，定时巡检关闭时仍执行历史清理。"),
                Ui.Row(Ui.Button("导出加密备份", ExportBackup), Ui.Button("导入恢复", ImportBackup)),
                Ui.Text("备份包含已保存的资产、设置与历史，使用独立口令加密。", 12, "Quiet"),
                new Expander { Header = "本机数据位置", Content = Ui.Value(_store.DirectoryPath) }), 14),
            Ui.Card(Ui.Stack(Ui.Section("关于"),
                Ui.Value($"VPSnode {typeof(Store).Assembly.GetName().Version!.ToString(3)}", 16, true),
                Ui.Value($"构建于 {System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(Store).Assembly).FirstOrDefault(a => a.Key == "BuildDateUtc")?.Value} UTC · {typeof(Store).Assembly.GetName().Version!.ToString(3)}", 12),
                Ui.Text("Windows 本地网络资产管理与检测工具。", brush: "Quiet"),
                Ui.Text("作者 cloud-mk · Apache-2.0", 12, "Quiet"),
                Ui.Value("https://github.com/cloud-mk/VPSnode", 12),
                Ui.Text("许可、署名和第三方依赖说明见随包 LICENSE、NOTICE、THIRD-PARTY-NOTICES。", 12, "Quiet")), 14));
        body.MaxWidth = 900;
        body.HorizontalAlignment = HorizontalAlignment.Stretch;
        CheckCore();
        return Ui.Stack(Header("设置", Ui.Button("保存设置", () => Save(), true), Ui.Button("退出应用", Quit)),
            feedback, body, Ui.Row(Ui.Button("保存设置", () => Save(), true)));
    }
}
