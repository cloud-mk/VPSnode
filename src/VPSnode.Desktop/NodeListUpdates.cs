using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VPSnode.Core;

namespace VPSnode.Desktop;

public partial class MainWindow
{
    private readonly SharedNodeColumns _nodeColumns = new();
    private readonly Dictionary<string, NodeListRow> _nodeRows = [];
    private readonly Dictionary<string, TextBlock> _nodeSummaries = [];
    private string[] _nodeLayout = [];
    private Button? _nodeCancel;

    private Node[] MatchingNodes() => _store.Nodes.Where(n =>
        (_nodeHostFilter.Length == 0 || n.HostId == _nodeHostFilter || (_nodeHostFilter == "-" && n.HostId.Length == 0)) &&
        (_search.Length == 0 || (n.Name + " " + n.Address + " " + n.Protocol).Contains(_search, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(n => n.Name).ToArray();

    private static string[] NodeLayout(Node[] nodes, Host[] hosts) => nodes.Select(n => n.Id + "|" + n.HostId)
        .Concat(hosts.Select(h => h.Id + "|" + h.Name + "|" + h.Hostname)).ToArray();

    // All callers are on the window Dispatcher. Updating observations never replaces the page,
    // ItemsSource or row objects, so text selection, sorting and scroll offsets remain intact.
    private void RefreshVisibleData()
    {
        if (_page != "nodes") { Render(); return; }
        var nodes = MatchingNodes(); var hosts = _store.Hosts.OrderBy(h => h.Name).ToArray();
        if (!_nodeLayout.SequenceEqual(NodeLayout(nodes, hosts))) { Render(); return; }
        var hostIndex = hosts.ToDictionary(h => h.Id);
        var last = _store.Latest(); var speeds = _store.LatestDownloads();
        var maximum = Math.Max(1, speeds.Values.Select(o => o.Value ?? 0).DefaultIfEmpty().Max());
        var task = _runningTask ?? _recentTask;
        var progress = task?.Items.Where(i => i.Kind is "node" or "speed").GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.Last());
        foreach (var n in nodes)
        {
            var value = NodeItem(n, hostIndex.GetValueOrDefault(n.HostId), last.GetValueOrDefault(("node", n.Id)));
            if (speeds.TryGetValue(n.Id, out var speed) && speed.Value is double rate)
                value = value with { Speed = rate, SpeedTip = $"{rate:0.#} Mbps · {Time(speed)}" };
            if (progress?.TryGetValue(n.Id, out var item) == true)
                value = value with { TaskState = item.State == "跳过" ? item.State + " · " + item.Detail : item.State };
            _nodeRows[n.Id].Update(value, maximum);
        }
        foreach (var group in nodes.GroupBy(n => n.HostId))
        {
            var text = $"{group.Count(n => IsNodeAvailable(n, last.GetValueOrDefault(("node", n.Id))))} 可用 · {group.Count(IsPaused)} 已暂停";
            if (_nodeSummaries[group.Key].Text != text) _nodeSummaries[group.Key].Text = text;
        }
        if (_nodeCancel != null) _nodeCancel.Visibility = _current != null || _runningTask != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed class NodeListRow(NodeRow value, double maximum) : INotifyPropertyChanged
    {
        private NodeRow _value = value;
        public Node Node => _value.Node;
        public string Name => _value.Name;
        public string Protocol => _value.Protocol;
        public string Port => _value.Port;
        public string Status => _value.Status;
        public string Delay => _value.Delay;
        public double? DelayValue => _value.DelayValue;
        public string Recent => _value.Recent;
        public double Speed => _value.Speed;
        public string SpeedText => _value.SpeedText;
        public string SpeedTip => _value.SpeedTip;
        public Visibility SpeedVisibility => _value.SpeedTip == "尚未测速" ? Visibility.Hidden : Visibility.Visible;
        public string TaskState => _value.TaskState;
        public bool CanTest => _value.CanTest;
        public double SpeedMaximum { get; private set; } = maximum;
        public event PropertyChangedEventHandler? PropertyChanged;

        public void Update(NodeRow next, double scale)
        {
            var before = _value;
            _value = next;
            void Changed(string name, object? old, object? current)
            {
                if (!Equals(old, current)) PropertyChanged?.Invoke(this, new(name));
            }
            Changed(nameof(Name), before.Name, Name); Changed(nameof(Protocol), before.Protocol, Protocol);
            Changed(nameof(Port), before.Port, Port); Changed(nameof(Status), before.Status, Status);
            Changed(nameof(Delay), before.Delay, Delay); Changed(nameof(DelayValue), before.DelayValue, DelayValue);
            Changed(nameof(Recent), before.Recent, Recent); Changed(nameof(Speed), before.Speed, Speed);
            Changed(nameof(SpeedText), before.SpeedText, SpeedText); Changed(nameof(SpeedTip), before.SpeedTip, SpeedTip);
            Changed(nameof(SpeedVisibility), before.SpeedTip == "尚未测速" ? Visibility.Hidden : Visibility.Visible, SpeedVisibility);
            Changed(nameof(TaskState), before.TaskState, TaskState); Changed(nameof(CanTest), before.CanTest, CanTest);
            var oldScale = SpeedMaximum; SpeedMaximum = scale; Changed(nameof(SpeedMaximum), oldScale, scale);
        }
    }
}

// One state owner for every node group and every visit to the page. Native WPF header
// resizing is retained; column notifications are detached when a page is replaced.
internal sealed class SharedNodeColumns
{
    private static readonly double[] Minimum = [160, 168, 64, 116, 100, 105, 218];
    private static readonly double[] Weight = [1.5, 1.2, 0, 1, 0, 0, 0];
    private readonly double[] _widths = [.. Minimum];
    private readonly bool[] _manual = new bool[Minimum.Length];
    private readonly List<(DataGridColumn Column, EventHandler Handler)> _subscriptions = [];
    private readonly List<DataGrid> _grids = [];
    private int[] _order = [0, 1, 2, 3, 4, 5, 6];
    private double _horizontalOffset;
    private bool _scrolling;
    private static readonly DependencyPropertyDescriptor WidthProperty = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn));
    private bool _applying;

    internal void Detach()
    {
        foreach (var (column, handler) in _subscriptions) WidthProperty.RemoveValueChanged(column, handler);
        _subscriptions.Clear();
        foreach (var grid in _grids)
        {
            grid.ColumnReordered -= Reordered;
            grid.Loaded -= Loaded;
            grid.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
        }
        _grids.Clear();
    }

    internal void Attach(DataGrid grid)
    {
        grid.SetValue(SharedTableColumns.ManagedProperty, true);
        for (int i = 0; i < Minimum.Length; i++)
        {
            var index = i; var column = grid.Columns[i];
            column.MinWidth = Minimum[i]; column.Width = new DataGridLength(_widths[i]);
            EventHandler changed = (_, _) =>
            {
                if (_applying || !column.Width.IsAbsolute || Math.Abs(column.Width.Value - _widths[index]) < .1) return;
                _manual[index] = true; _widths[index] = Math.Max(Minimum[index], column.Width.Value); Apply();
            };
            WidthProperty.AddValueChanged(column, changed); _subscriptions.Add((column, changed));
        }
        _grids.Add(grid); ApplyOrder(grid);
        grid.ColumnReordered += Reordered;
        grid.Loaded += Loaded;
        grid.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Scrolled));
    }

    private void ApplyOrder(DataGrid grid)
    {
        foreach (var index in Enumerable.Range(0, _order.Length).OrderBy(i => _order[i])) grid.Columns[index].DisplayIndex = _order[index];
    }

    private void Reordered(object? sender, DataGridColumnEventArgs args)
    {
        if (_applying || sender is not DataGrid source) return;
        _order = source.Columns.Select(c => c.DisplayIndex).ToArray();
        _applying = true;
        try { foreach (var grid in _grids.Where(g => g != source)) ApplyOrder(grid); }
        finally { _applying = false; }
    }

    private void Loaded(object sender, RoutedEventArgs args)
    {
        var grid = (DataGrid)sender;
        if (grid.Template.FindName("DG_ScrollViewer", grid) is ScrollViewer scroll) scroll.ScrollToHorizontalOffset(_horizontalOffset);
    }

    private void Scrolled(object sender, ScrollChangedEventArgs args)
    {
        if (_scrolling || args.HorizontalChange == 0 || sender is not DataGrid source ||
            !ReferenceEquals(args.OriginalSource, source.Template.FindName("DG_ScrollViewer", source))) return;
        _horizontalOffset = args.HorizontalOffset;
        _scrolling = true;
        try
        {
            foreach (var grid in _grids.Where(g => g != source))
                if (grid.Template.FindName("DG_ScrollViewer", grid) is ScrollViewer scroll) scroll.ScrollToHorizontalOffset(_horizontalOffset);
        }
        finally { _scrolling = false; }
    }

    internal void Fit(double available)
    {
        if (!double.IsFinite(available) || available <= 0) return;
        var totalWeight = Weight.Where((_, i) => !_manual[i]).Sum();
        var baseline = Minimum.Select((min, i) => _manual[i] ? _widths[i] : min).Sum();
        var extra = Math.Max(0, available - baseline);
        for (int i = 0; i < _widths.Length; i++)
            if (!_manual[i]) _widths[i] = Minimum[i] + (totalWeight > 0 ? Math.Floor(extra * Weight[i] / totalWeight) : 0);
        Apply();
    }

    private void Apply()
    {
        _applying = true;
        try
        {
            for (int i = 0; i < _subscriptions.Count; i++)
            {
                var column = _subscriptions[i].Column; var width = _widths[i % Minimum.Length];
                if (!column.Width.IsAbsolute || Math.Abs(column.Width.Value - width) > .1) column.Width = new DataGridLength(width);
            }
        }
        finally { _applying = false; }
    }
}
