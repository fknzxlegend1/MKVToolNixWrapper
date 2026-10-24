using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MKVToolNixWrapper.Dtos;

namespace MKVToolNixWrapper;

public sealed class FileStatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is FileStatusEnum status
            ? status switch
            {
                FileStatusEnum.FailedAnalysis => new SolidColorBrush(Color.Parse("#FCE8E7")),
                FileStatusEnum.WritingFile => new SolidColorBrush(Color.Parse("#FFF3D6")),
                FileStatusEnum.WrittenFile => new SolidColorBrush(Color.Parse("#E3F5EA")),
                FileStatusEnum.Error => new SolidColorBrush(Color.Parse("#F7DEDA")),
                _ => Brushes.Transparent
            }
            : Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
