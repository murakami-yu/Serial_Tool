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
            case nameof(Appearance.UiFontFamily):
            case nameof(Appearance.UiFontSize):
                ApplyUiFont();
                break;
        }
    }

    /// <summary>等宽字体默认链（App.xaml 的 MonoFont 初始值单一事实源镜像）。</summary>
    private static readonly FontFamily DefaultMonoFont = new("Cascadia Mono, Consolas, Courier New");

    /// <summary>应用界面字体：合法字体族 → 全局 UiFontFamily + 等宽区 MonoFont 都换成用户字体；
    /// 未设置/非法 → 移除全局资源（窗口回退系统默认字体）、MonoFont 恢复默认链。
    /// 字号合法（6~72）→ 写入 UiFontSize 资源；否则移除（回退系统默认 12px）。
    /// UiFontFamily/UiFontSize 两资源在 App.xaml 不预定义——不存在时 DynamicResource 不应用，天然等于「跟随系统」。</summary>
    private void ApplyUiFont()
    {
        var ap = Appearance.Instance;
        var name = ap.UiFontFamily.Trim();
        if (name.Length > 0 && IsKnownFont(name))
        {
            var font = new FontFamily(name);
            Resources["UiFontFamily"] = font;
            Resources["MonoFont"] = font;
        }
        else
        {
            Resources.Remove("UiFontFamily");
            Resources["MonoFont"] = DefaultMonoFont;
        }

        if (double.TryParse(ap.UiFontSize.Trim(), out var size) && size is >= 6 and <= 72)
            Resources["UiFontSize"] = size;
        else
            Resources.Remove("UiFontSize");
    }

    /// <summary>字体族合法性：系统已安装字体名单内（大小写不敏感），防手改配置的坏值。</summary>
    private static bool IsKnownFont(string name)
        => Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase));

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
