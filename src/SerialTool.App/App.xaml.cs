using System.Windows;
using System.Windows.Media;
using SerialTool.App.Services;

namespace SerialTool.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 崩溃/卡死诊断最先装：挂起看门狗 + 全局异常 → Logs/crash/（.log + .dmp 自转储）
        Services.CrashLogger.Install();
        // 控件级配色：外观单例属性变化 → 替换 App.xaml 主题画刷实例，DynamicResource 引用实时级联。
        // 不能用「StaticResource 共享实例 + 突变 Color」：样式/模板密封时会冻结引用到的画刷实例，
        // 突变冻结画刷抛 InvalidOperationException（曾在 LoadUiSettings 的 catch 里被静默吞掉，改色全无效）。
        // 加载顺序：本订阅早于 MainViewModel 构造（LoadUiSettings 写单例即触发应用）；
        // 值=默认时不触发，XAML 资源原值即默认，天然一致。
        Appearance.Instance.PropertyChanged += (_, a) => ApplyControlColor(a.PropertyName);
    }

    /// <summary>按外观属性名映射到主题画刷并替换实例；边框色变化时派生控件边框（加深）与分隔线（调亮）。</summary>
    private void ApplyControlColor(string? propertyName)
    {
        var ap = Appearance.Instance;
        switch (propertyName)
        {
            case nameof(Appearance.PanelHex):
                SetBrush("PanelBrush", ap.PanelHex);
                break;
            case nameof(Appearance.ButtonBgHex):
                SetBrush("ButtonBgBrush", ap.ButtonBgHex);
                break;
            case nameof(Appearance.BorderHex):
                if (TryParse(ap.BorderHex, out var border))
                {
                    SetBrush("BorderBrush", border);
                    // 派生：控件边框加深（默认 #C9C9C9→#ADADAD 精确复现）、分隔线调亮（→#E2E2E2 精确复现）
                    SetBrush("ControlBorderBrush", Darken(border, 0.1393));
                    SetBrush("SeparatorBrush", Lighten(border, 0.463));
                }
                break;
            case nameof(Appearance.TextHex):
                SetBrush("TextBrush", ap.TextHex);
                break;
            case nameof(Appearance.MutedHex):
                SetBrush("MutedBrush", ap.MutedHex);
                break;
            case nameof(Appearance.AccentHex):
                SetBrush("AccentBrush", ap.AccentHex);
                break;
            case nameof(Appearance.HoverHex):
                SetBrush("HoverBrush", ap.HoverHex);
                break;
            case nameof(Appearance.SelectedHex):
                SetBrush("SelectedBrush", ap.SelectedHex);
                break;
        }
    }

    private void SetBrush(string key, string hex)
    {
        if (TryParse(hex, out var c)) SetBrush(key, c);
    }

    private void SetBrush(string key, Color color)
    {
        // 整实例替换：旧实例可能已被样式/模板密封冻结（不可突变），DynamicResource 引用方自动级联新实例
        Resources[key] = new SolidColorBrush(color);
    }

    private static bool TryParse(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    /// <summary>向黑插值（factor 0.1393：#C9C9C9→#ADADAD）。</summary>
    private static Color Darken(Color c, double factor)
        => Color.FromRgb((byte)(c.R * (1 - factor)), (byte)(c.G * (1 - factor)), (byte)(c.B * (1 - factor)));

    /// <summary>向白插值（factor 0.463：#C9C9C9→#E2E2E2）。</summary>
    private static Color Lighten(Color c, double factor)
        => Color.FromRgb((byte)(c.R + (255 - c.R) * factor), (byte)(c.G + (255 - c.G) * factor), (byte)(c.B + (255 - c.B) * factor));
}
