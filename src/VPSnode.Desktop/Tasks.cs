using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VPSnode.Core;

namespace VPSnode.Desktop;

public partial class MainWindow
{
    private Inspection? _runningTask;
    private Inspection? _recentTask;
    private Inspection? _pendingTask;
    private int _updatePending;

    private Inspection[] TaskHistory(int limit) => _store.History("task", "history", DateTimeOffset.MinValue, limit)
        .Select(o => Wire.Decode<Inspection>(o.Detail)).ToArray();

    private void TaskChanged(Inspection task)
    {
        Volatile.Write(ref _pendingTask, task);
        if (Interlocked.Exchange(ref _updatePending, 1) != 0) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            Interlocked.Exchange(ref _updatePending, 0);
            if (_closing) return;
            var current = Volatile.Read(ref _pendingTask)!;
            _runningTask = current.State == "运行中" ? current : null;
            if (_runningTask == null) _recentTask = current;
            else if (_displayedStart != current.Started)
                _recentTask = TaskHistory(1).FirstOrDefault();
            _displayedStart = current.Started;
            UpdateTaskPanel();
            if (IsVisible && System.Windows.Input.Keyboard.FocusedElement is not TextBox && (_page is "tasks" or "nodes") && !OwnedWindows.OfType<Window>().Any(w => w.IsVisible))
            {
                var offset = Scroller.VerticalOffset;
                Render();
                Scroller.ScrollToVerticalOffset(offset);
            }
        }));
    }

    private DateTimeOffset _displayedStart;
    private void UpdateTaskPanel()
    {
        var task = _runningTask;
        TaskLabel.Text = task is null ? "空闲" : $"{task.Source} · {task.Label}";
        TaskCounts.Text = task is null ? "" : $"{task.Items.Length - task.Remaining} / {task.Items.Length} 完成\n成功 {task.Success} · 失败 {task.Failed} · 跳过 {task.Skipped}";
        TaskProgress.Visibility = StopTask.Visibility = task == null ? Visibility.Collapsed : Visibility.Visible;
        TaskProgress.Maximum = Math.Max(1, task?.Items.Length ?? 0);
        TaskProgress.Value = task == null ? 0 : task.Items.Length - task.Remaining;
        LastTask.Text = _recentTask is null ? "暂无已结束任务" :
            $"最近 {_recentTask.Started.LocalDateTime:HH:mm} · {_recentTask.State}\n成功 {_recentTask.Success} / 失败 {_recentTask.Failed} / 跳过 {_recentTask.Skipped}";
    }

    private readonly System.Collections.Generic.HashSet<DateTimeOffset> _expandedHistory = new();
    private int _historyLimit = 100;
    private StackPanel TasksPage()
    {
        var panel = Ui.Stack(Header("任务中心", Ui.Button("全测延迟", () => Run("nodes")),
            Ui.Button("全测下载速度", BatchSpeed)));
        if (_runningTask != null) panel.Children.Add(TaskDetails(_runningTask));
        foreach (var task in TaskHistory(_historyLimit))
            panel.Children.Add(new Expander { Header = $"{task.Started.LocalDateTime:MM-dd HH:mm:ss} · {task.Source} · {task.Label} · {task.State} · {task.Summary}",
                Content = null, Margin = new Thickness(0, 2, 0, 5), Tag = task });
        foreach (var entry in panel.Children.OfType<Expander>())
        {
            var time = ((Inspection)entry.Tag).Started;
            entry.Expanded += (_, _) => { _expandedHistory.Add(time); entry.Content ??= TaskDetails((Inspection)entry.Tag); };
            entry.Collapsed += (_, _) => _expandedHistory.Remove(time);
            entry.IsExpanded = _expandedHistory.Contains(time);
        }
        if (panel.Children.OfType<Expander>().Count() == _historyLimit)
            panel.Children.Add(Ui.Button("加载更早记录", () => { _historyLimit += 100; Render(); }));
        if (_runningTask == null && _recentTask == null) panel.Children.Add(Ui.Text("暂无巡检记录。"));
        return panel;
    }

    private FrameworkElement TaskDetails(Inspection task)
    {
        var table = Ui.Table(task.Items, [("目标", "Name", -1.2), ("本次状态", "State", 80), ("结果与原因", "Detail", -2.3)],
            ("查看", item =>
            {
                if (item.Kind is "node" or "speed" or "sni" && _store.Nodes.Any(n => n.Id == item.Id)) OpenNode(item.Id);
                else if (item.Kind is "host" or "inventory" && _store.Hosts.Any(h => h.Id == item.Id)) OpenHost(item.Id);
                else if (item.Kind == "web" && WebProbe.Targets(_store).FirstOrDefault(w => w.Id == item.Id) is WebTarget w) WebsiteResults(w);
                else Status("此目标已从台账移除");
            }));
        var heading = Ui.Stack(Ui.Section($"{task.Label} · {task.State}"), Ui.Value(task.Summary));
        if (task.State == "运行中")
        {
            heading.Children.Add(new ProgressBar { Minimum = 0, Maximum = Math.Max(1, task.Items.Length),
                Value = task.Items.Length - task.Remaining, Height = 5, Margin = new Thickness(0, 6, 0, 6) });
            heading.Children.Add(Ui.Text("当前：" + string.Join("、", task.Items.Where(i => i.State == "测试中").Select(i => i.Name)), brush: "Quiet"));
            heading.Children.Add(Ui.Button("取消当前任务", () => _monitor.CancelActive()));
        }
        heading.Children.Add(table);
        return Ui.Card(heading);
    }
}
