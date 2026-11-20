using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VPSnode.Desktop;

internal sealed class LatencyBadge : Border
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value),
        typeof(string), typeof(LatencyBadge), new PropertyMetadata("", (d, _) => ((LatencyBadge)d).Update()));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public LatencyBadge()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        CornerRadius = new CornerRadius(6);
        Padding = new Thickness(7, 3, 7, 3);
        Margin = new Thickness(2);
    }

    private void Update()
    {
        var text = Ui.Value(Value, 14, true);
        if (double.TryParse(Value.Split(' ')[0], NumberStyles.Float, CultureInfo.CurrentCulture, out var ms) && ms >= 0)
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ms <= 80 ? "#68E8A5" : ms <= 160 ? "#FFBD69" : "#8E293E"));
            text.Foreground = ms <= 160 ? new SolidColorBrush(Color.FromRgb(13, 28, 28)) : Brushes.White;
        }
        else { Background = Brushes.Transparent; Ui.Brush(text, Control.ForegroundProperty, "Quiet"); }
        Child = text;
        ToolTip = "最近一次成功检测延迟；检测时间与过时状态见同行。";
    }
}
