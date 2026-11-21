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

    public static readonly DependencyProperty MillisecondsProperty = DependencyProperty.Register(nameof(Milliseconds),
        typeof(double?), typeof(LatencyBadge), new PropertyMetadata(null, (d, _) => ((LatencyBadge)d).Update()));
    public double? Milliseconds { get => (double?)GetValue(MillisecondsProperty); set => SetValue(MillisecondsProperty, value); }

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
        text.TextWrapping = TextWrapping.NoWrap;
        var ms = Milliseconds ?? (double.TryParse(Value.Replace("ms", "").Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed) ? parsed : -1);
        if (Value != "—" && ms >= 0)
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(ms <= 80 ? "#68E8A5" : ms <= 160 ? "#FFBD69" : "#8E293E"));
            text.Foreground = ms <= 160 ? new SolidColorBrush(Color.FromRgb(13, 28, 28)) : Brushes.White;
        }
        else { Background = Brushes.Transparent; Ui.Brush(text, Control.ForegroundProperty, "Quiet"); }
        Child = text;
        ToolTip = "最近一次成功检测延迟；检测时间见同行。";
    }
}
