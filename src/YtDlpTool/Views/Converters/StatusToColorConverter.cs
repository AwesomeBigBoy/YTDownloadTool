using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using YtDlpTool.Domain.Models;

namespace YtDlpTool.Views.Converters;

public sealed class StatusToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not JobStatus s) return Brushes.Transparent;
        return s switch
        {
            JobStatus.Pending     => (Brush)Application.Current.FindResource("Brush.TextTertiary"),
            JobStatus.Downloading => (Brush)Application.Current.FindResource("Brush.Accent"),
            JobStatus.Completed   => (Brush)Application.Current.FindResource("Brush.Success"),
            JobStatus.Failed      => (Brush)Application.Current.FindResource("Brush.Danger"),
            _                     => Brushes.Gray
        };
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotImplementedException();
}
