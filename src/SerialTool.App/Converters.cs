using System.Globalization;
using System.Windows.Data;

namespace SerialTool.App;

/// <summary>bool ↔ ComboBox SelectedIndex（0/1）。</summary>
public sealed class BoolToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1 : 0;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is int i && i == 1;
}

/// <summary>启用状态 → 圆点（● 启用 / ○ 停用）。</summary>
public sealed class BoolToDotConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "●" : "○";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
