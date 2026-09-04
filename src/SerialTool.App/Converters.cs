using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace SerialTool.App;

/// <summary>bool ↔ ComboBox SelectedIndex（0/1）。</summary>
public sealed class BoolToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1 : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i == 1;
}

/// <summary>HEX 字符串 → 冻结 SolidColorBrush（颜色控件的色块填充用）；非法值回退黑色。</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString((string)value));
            b.Freeze();
            return b;
        }
        catch
        {
            return Brushes.Black;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>启用状态 → 圆点（● 启用 / ○ 停用）。</summary>
public sealed class BoolToDotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "●" : "○";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
