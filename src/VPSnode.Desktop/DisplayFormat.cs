using System;
using System.Globalization;
using System.Text.RegularExpressions;
namespace VPSnode.Desktop;
internal static partial class DisplayFormat
{
    internal static string Latency(double value) => Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture) + "ms";
    internal static string Details(string text) => Milliseconds().Replace(text, m =>
        double.TryParse(m.Groups[1].Value, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? Latency(value) : m.Value);
    [GeneratedRegex(@"(?<![\w.])(\d+(?:[.,]\d+)?)\s*ms\b")]
    private static partial Regex Milliseconds();
}
