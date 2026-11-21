using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace VPSnode.Desktop;

internal static class Ui
{
    internal static void Brush(FrameworkElement item, DependencyProperty property, string key) =>
        item.SetResourceReference(property, key);
    internal static Brush Color(string key) => (Brush)Application.Current.FindResource(key);

    internal static TextBlock Text(string value, int size = 14, string brush = "Text", bool bold = false)
    {
        var text = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        if (bold) text.FontWeight = FontWeights.SemiBold;
        Brush(text, TextBlock.ForegroundProperty, brush);
        return text;
    }

    internal static TextBox Value(string value, int size = 14, bool bold = false)
    {
        var box = new LedgerTextBox { Text = value, IsReadOnly = true, IsTabStop = false,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Cursor = System.Windows.Input.Cursors.IBeam };
        return box;
    }

    internal static StackPanel Stack(params UIElement[] elements)
    {
        var stack = new StackPanel();
        foreach (var element in elements) stack.Children.Add(element);
        return stack;
    }

    internal static WrapPanel Row(params UIElement[] elements)
    {
        var row = new WrapPanel();
        foreach (var element in elements) row.Children.Add(element);
        return row;
    }

    internal static Button Button(string label, Action click, bool primary = false)
    {
        var button = new Button { Content = label };
        if (primary)
        {
            Brush(button, Control.BackgroundProperty, "Accent");
            Brush(button, Control.BorderBrushProperty, "Accent");
            button.Foreground = Brushes.White;
        }
        button.Click += (_, _) => click();
        return button;
    }

    internal static Border Card(UIElement content, int padding = 10)
    {
        var card = new Border { Child = content, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(padding), Margin = new Thickness(0, 0, 0, 8) };
        Brush(card, Border.BackgroundProperty, "Surface");
        Brush(card, Border.BorderBrushProperty, "Line");
        return card;
    }

    internal static TextBlock Section(string value)
    {
        var label = Text(value, 16, bold: true);
        label.Margin = new Thickness(0, 2, 0, 6);
        return label;
    }

    internal static Grid Columns(params UIElement[] items)
    {
        var grid = new Grid();
        for (var i = 0; i < items.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(items[i], i);
            if (i + 1 < items.Length && items[i] is FrameworkElement element)
                element.Margin = new Thickness(element.Margin.Left, element.Margin.Top, 8, element.Margin.Bottom);
            grid.Children.Add(items[i]);
        }
        return grid;
    }

    internal static DataGrid Table<T>(IEnumerable<T> rows, (string Header, string Property, double Width)[] columns,
        params (string Label, Action<T> Action)[] actions)
    {
        var grid = new DataGrid { ItemsSource = rows, MaxHeight = double.PositiveInfinity };
        ScrollViewer.SetHorizontalScrollBarVisibility(grid, ScrollBarVisibility.Auto);
        foreach (var (header, property, width) in columns)
        {
            var length = width > 0 ? new DataGridLength(width) : new DataGridLength(Math.Abs(width), DataGridLengthUnitType.Star);
            if (property == "Delay")
            {
                var badge = new FrameworkElementFactory(typeof(LatencyBadge));
                badge.SetBinding(LatencyBadge.ValueProperty, new Binding(property));
                badge.SetBinding(LatencyBadge.MillisecondsProperty, new Binding("DelayValue"));
                grid.Columns.Add(new DataGridTemplateColumn { Header = header, Width = length,
                    MinWidth = 80, SortMemberPath = property, CellTemplate = new DataTemplate { VisualTree = badge } });
                continue;
            }
            if (property is "Cert" or "On80" or "On443" or "ListenerState" or "Comparison")
            {
                var pill = new FrameworkElementFactory(typeof(StatusPill));
                pill.SetBinding(StatusPill.ValueProperty, new Binding(property));
                grid.Columns.Add(new DataGridTemplateColumn { Header = header, Width = length,
                    MinWidth = width > 0 ? width : 88, SortMemberPath = property, CellTemplate = new DataTemplate { VisualTree = pill } });
                continue;
            }
            var cell = ValueCell(property);
            grid.Columns.Add(new DataGridTemplateColumn { Header = header, SortMemberPath = property,
                Width = length, MinWidth = width > 0 ? width : 65,
                CellTemplate = new DataTemplate { VisualTree = cell } });
        }
        if (actions.Length > 0)
        {
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            foreach (var (label, action) in actions)
            {
                var button = new FrameworkElementFactory(typeof(Button));
                button.SetValue(ContentControl.ContentProperty, label);
                if (label is "测延迟" or "测下载") button.SetBinding(UIElement.IsEnabledProperty, new Binding("CanTest"));
                button.SetValue(Control.PaddingProperty, new Thickness(8, 4, 8, 4));
                button.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler((sender, _) =>
                {
                    if (sender is Button { DataContext: T item }) action(item);
                }));
                row.AppendChild(button);
            }
            grid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "操作", Width = DataGridLength.SizeToCells, MinWidth = actions.Length * 49 + 4,
                CellTemplate = new DataTemplate { VisualTree = row }
            });
        }
        grid.PreviewMouseWheel += (_, e) =>
        {
            for (DependencyObject? parent = VisualTreeHelper.GetParent(grid); parent != null; parent = VisualTreeHelper.GetParent(parent))
                if (parent is ScrollViewer outer)
                {
                    outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta / 120d * Math.Max(1, SystemParameters.WheelScrollLines) * 16);
                    e.Handled = true;
                    break;
                }
        };
        grid.PreviewMouseRightButtonUp += (_, e) =>
        {
            for (var hit = e.OriginalSource as DependencyObject; hit is Visual; hit = VisualTreeHelper.GetParent(hit))
            {
                if (hit is TextBox or System.Windows.Controls.Button) return;
                if (hit is not DataGridCell cell) continue;
                var text = string.Join(Environment.NewLine, VisibleValues(cell));
                if (text.Length == 0) return;
                var menu = new ContextMenu { PlacementTarget = cell, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
                var copy = new MenuItem { Header = "复制" };
                copy.Click += (_, _) => LedgerTextBox.CopyValue(text);
                menu.Items.Add(copy);
                menu.IsOpen = true;
                e.Handled = true;
                return;
            }
        };
        grid.Loaded += FitAfterLayout;
        return grid;
    }

    // A DataGrid inside the page ScrollViewer initially measures star columns at their minimums.
    // Use the arranged width, keeping fixed columns and the action buttons readable.
    private static IEnumerable<string> VisibleValues(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is UIElement { IsVisible: false }) continue;
            if (child is LedgerTextBox text && text.Text.Length > 0) yield return text.Text;
            else foreach (var value in VisibleValues(child)) yield return value;
        }
    }

    private static void FitAfterLayout(object sender, RoutedEventArgs args)
    {
        var grid = (DataGrid)sender;
        grid.Loaded -= FitAfterLayout;
        var flexible = grid.Columns.Where(c => c.Width.IsStar).Select(c => (Column: c, Weight: c.Width.Value)).ToArray();
        if (flexible.Length == 0) return;
        var fixedColumns = grid.Columns.Except(flexible.Select(f => f.Column)).ToArray();
        var weightSum = flexible.Sum(f => f.Weight);
        void Fit()
        {
            var scroller = grid.Template.FindName("DG_ScrollViewer", grid) as ScrollViewer;
            var viewport = scroller?.ViewportWidth is > 0 ? scroller.ViewportWidth : grid.ActualWidth - 3;
            var available = viewport - fixedColumns.Sum(c => Math.Max(c.MinWidth, c.ActualWidth));
            var extra = Math.Max(0, available - flexible.Sum(f => f.Column.MinWidth));
            foreach (var (column, weight) in flexible)
                column.Width = new DataGridLength(column.MinWidth + Math.Floor(extra * weight / weightSum));
        }
        Fit();
        grid.SizeChanged += (_, e) => { if (e.WidthChanged || e.HeightChanged) Fit(); };
    }

    private static FrameworkElementFactory ValueCell(string field)
    {
        var cell = new FrameworkElementFactory(typeof(LedgerTextBox));
        cell.SetValue(TextBox.IsReadOnlyProperty, true);
        cell.SetValue(Control.IsTabStopProperty, false);
        cell.SetValue(Control.BorderThicknessProperty, new Thickness(0));
        cell.SetValue(Control.BackgroundProperty, Brushes.Transparent);
        cell.SetValue(Control.PaddingProperty, new Thickness(3, 0, 3, 0));
        cell.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        cell.SetBinding(TextBox.TextProperty, new Binding(field) { Mode = BindingMode.OneWay });
        cell.SetBinding(FrameworkElement.ToolTipProperty, new Binding(field));
        return cell;
    }

    internal static DataGridTemplateColumn NavigationColumn(string title, string field, string secondary, double width, double minimum, double speedMaximum = 0)
    {
        var group = new FrameworkElementFactory(typeof(StackPanel));
        group.SetValue(FrameworkElement.MarginProperty, new Thickness(5, 5, 5, 5));
        var cell = ValueCell(field);
        var style = new Style(typeof(LedgerTextBox));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("Text")));
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.ForegroundProperty, new DynamicResourceExtension("ActionText")));
        style.Triggers.Add(hover);
        cell.SetValue(FrameworkElement.StyleProperty, style);
        group.AppendChild(cell);
        if (secondary.Length > 0)
        {
            var detail = ValueCell(secondary);
            detail.SetResourceReference(Control.ForegroundProperty, "Quiet");
            detail.SetValue(Control.FontSizeProperty, 12d);
            group.AppendChild(detail);
        }
        if (speedMaximum > 0)
        {
            var strip = new FrameworkElementFactory(typeof(ProgressBar));
            strip.SetValue(FrameworkElement.HeightProperty, 3d);
            strip.SetValue(FrameworkElement.MarginProperty, new Thickness(3, 3, 3, 0));
            strip.SetBinding(ProgressBar.MaximumProperty, new Binding("SpeedMaximum"));
            strip.SetValue(Control.BorderThicknessProperty, new Thickness(0));
            strip.SetValue(Control.BackgroundProperty, Brushes.Transparent);
            strip.SetResourceReference(ProgressBar.ForegroundProperty, "Speed");
            strip.SetBinding(ProgressBar.ValueProperty, new Binding("Speed") { Mode = BindingMode.OneWay });
            strip.SetBinding(UIElement.VisibilityProperty, new Binding("SpeedVisibility"));
            strip.SetBinding(FrameworkElement.ToolTipProperty, new Binding("SpeedTip"));
            group.AppendChild(strip);
        }
        return new DataGridTemplateColumn { Header = title, SortMemberPath = field, MinWidth = minimum,
            Width = width < 0 ? new DataGridLength(-width, DataGridLengthUnitType.Star) : new DataGridLength(width),
            CellTemplate = new DataTemplate { VisualTree = group } };
    }

    internal static DataGridTemplateColumn PairedColumn(string title, string first, string second, double width,
        bool firstBadge = false, bool secondBadge = false)
    {
        var group = new FrameworkElementFactory(typeof(StackPanel));
        group.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        group.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 3, 4, 3));
        void Line(string field, bool badge, bool secondary)
        {
            if (field.Length == 0) return;
            var item = badge ? new FrameworkElementFactory(typeof(StatusPill)) : ValueCell(field);
            if (badge) item.SetBinding(StatusPill.ValueProperty, new Binding(field));
            if (!badge)
            {
                item.SetValue(TextBox.TextWrappingProperty, TextWrapping.Wrap);
                item.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1));
                item.SetResourceReference(Control.ForegroundProperty, secondary ? "Quiet" : "Text");
                item.SetBinding(FrameworkElement.ToolTipProperty, new Binding(field));
            }
            group.AppendChild(item);
        }
        Line(first, firstBadge, false);
        Line(second, secondBadge, true);
        return new DataGridTemplateColumn { Header = title, Width = new DataGridLength(width, DataGridLengthUnitType.Star),
            MinWidth = 80, SortMemberPath = first, CellTemplate = new DataTemplate { VisualTree = group } };
    }

    internal static void Theme(string selected)
    {
        var dark = selected == "dark";
        string[] keys = ["Surface", "Canvas", "MutedSurface", "Text", "Quiet", "Line", "Accent", "Good", "Warn", "Bad"];
        string[] colors = dark
            ? ["#101825", "#080D15", "#172130", "#EDF3FF", "#A5B5CD", "#253143", "#496AE8", "#76D6A8", "#FFC178", "#FF9A9D"]
            : ["#FFFFFF", "#EFF3F9", "#E6EDF7", "#152740", "#485D76", "#C9D6E7", "#1D55C7", "#16724E", "#8E5100", "#BB343B"];
        var res = Application.Current.Resources;
        for (int i = 0; i < keys.Length; i++)
            res[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        foreach (var (key, light, night) in new[] { ("GoodFill","#E1F4E9","#183D31"), ("WarnFill","#FFF1D6","#44341E"),
            ("BadFill","#FCE5E6","#45272F"), ("QuietFill","#E9EFF6","#293447") })
            res[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? night : light));
        res["TextSelection"] = dark ? Brushes.Gold : Brushes.Navy;
        res["SelectedText"] = dark ? Brushes.Black : Brushes.White;
        res["Speed"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#9580FF" : "#6247D7"));
        res["ActionText"] = new SolidColorBrush(dark ? Colors.LightSkyBlue : Colors.DarkBlue);
        res["GroupAlt"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#182333" : "#E8EDF3"));
        res[SystemColors.WindowBrushKey] = res["Surface"];
        res[SystemColors.WindowTextBrushKey] = res["Text"];
        res[SystemColors.HighlightBrushKey] = res["Accent"];
    }
}
