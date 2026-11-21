using System.Windows;
using System.Windows.Controls;

namespace VPSnode.Desktop;

internal sealed class StatusPill : Border
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value),
        typeof(string), typeof(StatusPill), new PropertyMetadata("", (d, _) => ((StatusPill)d).Update()));
    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public StatusPill()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(9, 3, 9, 3);
        Margin = new Thickness(3, 2, 3, 2);
    }

    private void Update()
    {
        var label = Value ?? "";
        var tone = label.StartsWith("✓") ? "Good" :
            label.StartsWith("!") ? "Bad" : label.StartsWith("◇") ? "Warn" : "Quiet";
        Ui.Brush(this, BackgroundProperty, tone + "Fill");
        var text = Ui.Value(label, 13, true);
        Ui.Brush(text, Control.ForegroundProperty, tone);
        Child = text;
        ToolTip = label;
    }
}
