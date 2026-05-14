using System.Globalization;
using System.Windows.Data;
using YtDlpTool.ViewModels;

namespace YtDlpTool.Views.Converters;

public sealed class QueueMetaConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not QueueItemViewModel v) return "";
        var pct = $"{v.ProgressPercent:0.#}%";
        var speed = v.BytesPerSecond is { } b ? $" · {FormatSpeed(b)}" : "";
        var eta = v.Eta is { } t ? $" · 剩餘 {t:hh\\:mm\\:ss}" : "";
        var mode = $" · {v.QualityLabel} {v.ModeLabel}";
        var failure = v.FailureReason is { } r ? $"  ⚠ {r}" : "";
        return pct + speed + eta + mode + failure;
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
    private static string FormatSpeed(long b)
    {
        double v = b; var u = "B/s";
        if (v >= 1024) { v /= 1024; u = "KB/s"; }
        if (v >= 1024) { v /= 1024; u = "MB/s"; }
        return $"{v:0.#} {u}";
    }
}
