using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace VPSnode.Desktop;

// Tables with identical fields in one window share user widths; the node page keeps
// its established column/order/scroll coordinator.
internal sealed class SharedTableColumns
{
    internal static readonly DependencyProperty ManagedProperty = DependencyProperty.RegisterAttached(
        "Managed", typeof(bool), typeof(SharedTableColumns), new PropertyMetadata(false));
    internal static readonly DependencyProperty AutoFitProperty = DependencyProperty.RegisterAttached(
        "AutoFit", typeof(bool), typeof(SharedTableColumns), new PropertyMetadata(false));
    private static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group", typeof(SharedTableColumns), typeof(SharedTableColumns));
    private static readonly ConditionalWeakTable<Window, Dictionary<string, SharedTableColumns>> Windows = new();
    private static readonly DependencyPropertyDescriptor WidthProperty = DependencyPropertyDescriptor.FromProperty(DataGridColumn.WidthProperty, typeof(DataGridColumn))!;
    private readonly List<DataGrid> _grids = [];
    private readonly Dictionary<DataGridColumn, EventHandler> _handlers = [];
    private double[] _widths = [];
    private readonly HashSet<int> _manual = [];
    private bool _applying;

    internal static void Attach(DataGrid grid)
    {
        if ((bool)grid.GetValue(ManagedProperty) || Window.GetWindow(grid) is not Window window) return;
        var key = string.Join("|", grid.Columns.Select(c => $"{c.Header}/{c.SortMemberPath}/{c.MinWidth}"));
        var groups = Windows.GetOrCreateValue(window);
        if (!groups.TryGetValue(key, out var group)) groups[key] = group = new();
        group.Add(grid);
    }

    private void Add(DataGrid grid)
    {
        if (_grids.Contains(grid)) return;
        grid.SetValue(GroupProperty, this);
        _grids.Add(grid);
        if (_widths.Length == 0) _widths = grid.Columns.Select(c => c.ActualWidth).ToArray();
        else Apply();
        for (int i = 0; i < grid.Columns.Count; i++)
        {
            var index = i; var column = grid.Columns[i];
            EventHandler changed = (_, _) =>
            {
                if (_applying || !column.Width.IsAbsolute) return;
                if (!(bool)grid.GetValue(AutoFitProperty)) _manual.Add(index);
                _widths[index] = Math.Max(column.MinWidth, column.Width.Value); Apply();
            };
            _handlers[column] = changed; WidthProperty.AddValueChanged(column, changed);
        }
        grid.Unloaded += Unloaded;
    }

    internal static bool IsManual(DataGrid grid, DataGridColumn column) =>
        grid.GetValue(GroupProperty) is SharedTableColumns group && group._manual.Contains(grid.Columns.IndexOf(column));
    internal static bool IsApplying(DataGrid grid) =>
        grid.GetValue(GroupProperty) is SharedTableColumns { _applying: true };

    private void Unloaded(object sender, RoutedEventArgs args)
    {
        var grid = (DataGrid)sender;
        foreach (var column in grid.Columns)
            if (_handlers.Remove(column, out var handler)) WidthProperty.RemoveValueChanged(column, handler);
        grid.Unloaded -= Unloaded; _grids.Remove(grid);
    }

    private void Apply()
    {
        _applying = true;
        try
        {
            foreach (var grid in _grids)
                for (int i = 0; i < _widths.Length; i++)
                    if (Math.Abs(grid.Columns[i].ActualWidth - _widths[i]) > .1) grid.Columns[i].Width = new DataGridLength(_widths[i]);
        }
        finally { _applying = false; }
    }
}
