using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace WinHardwareMultitool.Converters;

/// <summary>Colors a log line by the keywords the ViewModel already writes into it - avoids
/// threading a separate severity enum through every LogEntries.Add() call just for this.</summary>
public sealed class LogSeverityToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush CriticalBrush = Freeze(0xE7, 0x4C, 0x3C);
    private static readonly SolidColorBrush WarningBrush = Freeze(0xF1, 0xC4, 0x0F);
    private static readonly SolidColorBrush DefaultBrush = Freeze(0xD4, 0xD6, 0xDA);

    private static SolidColorBrush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string line = value as string ?? string.Empty;

        if (line.Contains("ACİL DURUM", StringComparison.OrdinalIgnoreCase))
            return CriticalBrush;

        if (line.Contains("UYARI", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("iptal edildi", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("başlatılamadı", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("hata", StringComparison.OrdinalIgnoreCase))
            return WarningBrush;

        return DefaultBrush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
