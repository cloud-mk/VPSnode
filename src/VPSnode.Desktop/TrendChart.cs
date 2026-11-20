using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using VPSnode.Core;

namespace VPSnode.Desktop;

internal sealed class TrendChart : FrameworkElement
{
    private readonly Observation[] _samples;
    private readonly DateTimeOffset _start;
    private readonly DateTimeOffset _end;
    private readonly TimeSpan _interval;
    private readonly bool _bars;

    internal TrendChart(Observation[] samples, DateTimeOffset start, DateTimeOffset end, TimeSpan interval, bool bars)
    {
        _samples = samples;
        _start = start;
        _end = end;
        _interval = interval;
        _bars = bars;
        Height = 230;
        ToolTip = bars ? "每柱为一次手动下载测速" : "成功样本用曲线连接，缺测时留空。";
        Focusable = true;
    }

    private void DrawText(DrawingContext drawing, string value, Point point, string brush)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei UI"), 11, Ui.Color(brush), dpi);
        drawing.DrawText(text, point);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var values = _samples.Where(x => x.Value.HasValue).ToArray();
        if (values.Length == 0)
        {
            DrawText(drawingContext, _bars ? "尚未测速" : "尚无延迟数据", new Point(22, 42), "Quiet");
            return;
        }
        double right = Math.Max(58, ActualWidth - 10);
        const double left = 46, top = 24, bottom = 198;
        var maximum = values.Max(x => x.Value ?? 0);
        var size = Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(Math.Max(1, maximum)))) / 2);
        var ceiling = Math.Max(size, Math.Ceiling(maximum * 1.12 / size) * size);
        var grid = new Pen(Ui.Color("Line"), 1);
        var active = new Pen(Ui.Color("Accent"), 2);
        for (int i = 0; i <= 4; i++)
        {
            var y = top + (bottom - top) * i / 4;
            drawingContext.DrawLine(grid, new Point(left, y), new Point(right, y));
            DrawText(drawingContext, (ceiling * (4 - i) / 4).ToString("0"), new Point(5, y - 8), "Quiet");
        }
        DrawText(drawingContext, _bars ? "Mbps" : "ms", new Point(4, 0), "Quiet");
        if (_bars)
        {
            var space = (right - left) / values.Length;
            for (int i = 0; i < values.Length; i++)
            {
                var value = values[i];
                var h = (bottom - top) * value.Value!.Value / ceiling;
                var width = Math.Min(19, space * .55);
                var center = left + space * (i + .5);
                drawingContext.DrawRectangle(Ui.Color("Speed"), null, new Rect(center - width / 2, bottom - h, width, h));
                if (values.Length <= 6 || i % 2 == 0)
                    DrawText(drawingContext, value.At.LocalDateTime.ToString("HH:mm"), new Point(Math.Clamp(center - 16, left, right - 32), bottom + 6), "Quiet");
            }
            return;
        }
        double X(DateTimeOffset time) => left + (right - left) * Math.Clamp(
            (time - _start).TotalSeconds / Math.Max(1, (_end - _start).TotalSeconds), 0, 1);
        Point? previous = null;
        Observation? last = null;
        foreach (var value in _samples)
        {
            if (value.Value is not double delay) { previous = null; last = null; continue; }
            var point = new Point(X(value.At), bottom - (bottom - top) * delay / ceiling);
            if (previous is Point p && last is not null && value.Context == last.Context && value.At - last.At <= _interval * 2.2)
                drawingContext.DrawLine(active, p, point);
            else drawingContext.DrawEllipse(Ui.Color("Accent"), null, point, 2, 2);
            previous = point;
            last = value;
        }
        for (int i = 0; i <= 3; i++)
        {
            var time = _start + (_end - _start) * (i / 3d);
            DrawText(drawingContext, time.LocalDateTime.ToString((_end - _start).TotalDays > 1.1 ? "MM-dd" : "HH:mm"),
                new Point(Math.Min(X(time), right - 34), bottom + 6), "Quiet");
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var success = _samples.Where(x => x.Value.HasValue).ToArray();
        if (success.Length == 0) return;
        var position = Math.Clamp((e.GetPosition(this).X - 46) / Math.Max(1, ActualWidth - 56), 0, .999);
        var sample = _bars ? success[(int)(success.Length * position)] :
            success.MinBy(x => Math.Abs((x.At - (_start + (_end - _start) * position)).TotalSeconds))!;
        ToolTip = $"{sample.At.LocalDateTime:g} · {sample.Value:0.#} {(_bars ? "Mbps" : "ms")}";
    }
}
