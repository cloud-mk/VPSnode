using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VPSnode.Core;

namespace VPSnode.Desktop;

public partial class MainWindow
{
    private void BatchSpeed()
    {
        var count = _store.Nodes.Count(n => !IsPaused(n));
        var bytes = Math.Clamp(_store.Settings.SpeedBytes, 100_000, 100_000_000);
        var body = Ui.Stack(Ui.Section("全测下载速度"),
            Ui.Text($"全部 {count} 个未暂停节点，包括未归属节点。配置或核心缺失的项目会跳过并列明原因。"),
            Ui.Text($"并发 1；每节点最多 {bytes / 1_000_000d:0.#} MB、45 秒。应用读取总上限约 {count * (long)bytes / 1_000_000d:0.#} MB，实际出站含协议开销。", brush: "Quiet"));
        var dialog = Dialog("批量下载流量预算", body, 570, 260);
        body.Children.Add(Ui.Row(Ui.Button("开始全测", () => dialog.DialogResult = true, true), Ui.Button("取消", () => dialog.Close())));
        if (dialog.ShowDialog() == true) Run("speeds");
    }

    private string? BackupPassword(bool exporting)
    {
        var password = new PasswordBox { Margin = new Thickness(0, 5, 0, 10), MaxLength = 1024 };
        var repeat = new PasswordBox { Margin = new Thickness(0, 5, 0, 10), MaxLength = 1024 };
        var body = Ui.Stack(Ui.Text(exporting ? "为备份设置至少 12 个字符的独立口令，请妥善保存。忘记口令无法恢复。" : "输入这个备份文件的加密口令。"), Ui.Text("备份口令"), password);
        System.Windows.Automation.AutomationProperties.SetName(password, "备份口令");
        System.Windows.Automation.AutomationProperties.SetName(repeat, "再次输入口令");
        if (exporting) { body.Children.Add(Ui.Text("再次输入口令")); body.Children.Add(repeat); }
        var error = Ui.Text("", brush: "Bad");
        body.Children.Add(error);
        var dialog = Dialog(exporting ? "加密导出" : "解密备份", body, 530, exporting ? 335 : 250);
        body.Children.Add(Ui.Row(Ui.Button("继续", () =>
        {
            if (password.Password.Length == 0 || exporting && password.Password.Length < 12) { error.Text = "口令至少 12 个字符。"; return; }
            if (exporting && password.Password != repeat.Password) { error.Text = "两次口令不一致。"; return; }
            dialog.DialogResult = true;
        }, true), Ui.Button("取消", () => dialog.Close())));
        var accepted = dialog.ShowDialog() == true;
        var result = accepted ? password.Password : null;
        password.Clear(); repeat.Clear();
        return result;
    }

    private async Task<T> Maintenance<T>(Func<T> action)
    {
        _timer.Stop();
        Status("等待当前巡检结束后处理数据…");
        try { return await Task.Run(() => _monitor.Maintain(action, _app.Token)); }
        finally { if (!_closing) _timer.Start(); }
    }

    private async void ExportBackup()
    {
        var picker = new SaveFileDialog { Filter = "VPSnode 加密备份|*.vpsbackup", FileName = $"VPSnode-{DateTime.Now:yyyyMMdd}.vpsbackup" };
        if (picker.ShowDialog(this) != true) return;
        var password = BackupPassword(true);
        if (password is null) return;
        try
        {
            await Maintenance(() => { _store.ExportBackup(picker.FileName, password); return true; });
            Status("完整加密备份已导出；外部 SSH 私钥文件请另行安全迁移。");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private async void ImportBackup()
    {
        var picker = new OpenFileDialog { Filter = "VPSnode 加密备份|*.vpsbackup|所有文件|*.*" };
        if (picker.ShowDialog(this) != true) return;
        var password = BackupPassword(false);
        if (password is null) return;
        try
        {
            var preview = await Task.Run(() => _store.PreviewBackup(picker.FileName, password));
            var strategy = ConfirmBackupImport(preview);
            if (strategy is null) return;
            var overwrite = strategy.Value;
            await Maintenance(() => _store.ImportBackup(picker.FileName, password, overwrite));
            Ui.Theme(_store.Settings.Theme);
            _recentTask = TaskHistory(1).FirstOrDefault(); UpdateTaskPanel();
            Render(); Status("数据导入完成。请核对本机 v2rayN 目录、SSH 别名和私钥路径。");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private bool? ConfirmBackupImport(BackupPreview preview)
    {
        var body = Ui.Stack(Ui.Section("导入预览"),
            Ui.Text($"VPS {preview.Hosts} · 节点 {preview.Nodes} · 网站 {preview.Websites}\n历史 {preview.Observations} · 排队任务 {preview.Requests} · 通知记录 {preview.Notices}"),
            Ui.Text(string.Join("\n", preview.Warnings), brush: "Warn"),
            Ui.Text("导入完成后自动巡检将继续；请先确认备份中的目标、SSH 入口和本机依赖适用于此电脑。", brush: "Quiet"));
        var merge = new RadioButton { Content = "合并：同 ID 保留本机数据，追加新记录及不重复历史", IsChecked = true, Margin = new Thickness(0, 12, 0, 8) };
        var replace = new RadioButton { Content = "替换：用备份完整替换当前台账、设置与历史", Margin = new Thickness(0, 4, 0, 8) };
        body.Children.Add(merge); body.Children.Add(replace);
        var acknowledge = new CheckBox { Content = "我已导出当前台账，确认完整替换" };
        acknowledge.Visibility = Visibility.Collapsed;
        replace.Checked += (_, _) => acknowledge.Visibility = Visibility.Visible;
        merge.Checked += (_, _) => acknowledge.Visibility = Visibility.Collapsed;
        body.Children.Add(acknowledge);
        var message = Ui.Text("", brush: "Bad"); body.Children.Add(message);
        var dialog = Dialog("确认导入数据", body, 630, 460);
        body.Children.Add(Ui.Row(Ui.Button("导入", () =>
        {
            if (replace.IsChecked == true && acknowledge.IsChecked != true) { message.Text = "替换会覆盖现有记录，请先备份并勾选确认。"; return; }
            dialog.DialogResult = true;
        }, true), Ui.Button("取消", () => dialog.Close())));
        if (dialog.ShowDialog() != true) return null;
        return replace.IsChecked == true;
    }

    private void DuplicateNodes()
    {
        try { ShowDuplicateNodes(); }
        catch (Exception ex) { Fail(ex); }
    }

    private void ShowDuplicateNodes()
    {
        var candidates = _store.FindDuplicateCandidates();
        var body = Ui.Stack(Ui.Section("核对重复节点"), Ui.Text("只有已核实的身份与路由相符才自动关联。下列项目保留独立记录，先比较差异再决定。", brush: "Quiet"));
        var window = Dialog("核对重复节点", body, 820, 580);
        foreach (var match in candidates)
        {
            var left = _store.Nodes.First(n => n.Id == match.LeftId);
            var right = _store.Nodes.First(n => n.Id == match.RightId);
            var group = Ui.Stack(Ui.Section(left.Name + " ↔ " + right.Name), Ui.Text(match.Reason),
                Ui.Table(match.Differences, [("字段", "Field", -1), ("保留记录", "Left", -2), ("待关联记录", "Right", -2)]));
            group.Children.Add(Ui.Row(Ui.Button("查看左侧", () => { window.Close(); OpenNode(left.Id); }),
                Ui.Button("查看右侧", () => { window.Close(); OpenNode(right.Id); })));
            if (match.CanMerge) group.Children.Add(Ui.Button("确认关联并保留左侧配置", async () =>
            {
                if (MessageBox.Show(window, "合并后保留左侧有效客户端配置、两侧来源及全部历史；右侧独立记录将被关联。确认已核对关键差异？",
                    "确认节点关联", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                try
                {
                    await Maintenance(() => { _store.MergeNodes(left.Id, right.Id); return true; });
                    window.Close(); Render(); Status("节点已关联，来源与历史已保留。");
                }
                catch (Exception ex) { Fail(ex); }
            }));
            body.Children.Add(Ui.Card(group));
        }
        if (candidates.Length == 0) body.Children.Add(Ui.Text("没有需要核对的相似记录。"));
        body.Children.Add(Ui.Button("关闭", () => window.Close()));
        window.ShowDialog();
    }
}
