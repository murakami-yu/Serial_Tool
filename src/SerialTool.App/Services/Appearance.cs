using CommunityToolkit.Mvvm.ComponentModel;

namespace SerialTool.App.Services;

/// <summary>外观设置单例：各窗口背景色（HEX 字符串），x:Static 绑定源。
/// 各窗口 DataContext 类型不统一（TerminalWindow 甚至没有 DataContext，对话框各有自己的），
/// 单例 + Source={x:Static} 是唯一让所有窗口 XAML 绑定路径一致的方案。
/// 持久化由 MainViewModel 桥接（ui_settings.json）：加载时写入本单例，属性变化时落盘。</summary>
public sealed partial class Appearance : ObservableObject
{
    public static Appearance Instance { get; } = new();

    public const string DefaultMainBg = "#F4F4F4";        // 主窗口（含全部对话框，跟随主窗）
    public const string DefaultTerminalBg = "#F4F4F4";    // 终端窗口框架
    public const string DefaultChartBg = "#F4F4F4";       // 波形窗口
    public const string DefaultTemplateBg = "#F4F4F4";    // 模板编辑器
    public const string DefaultTermContentBg = "#1E1E1E"; // 终端内容区（Campbell 深底；ANSI 调色板不动）

    /// <summary>主窗口背景（对话框跟随）。</summary>
    [ObservableProperty]
    private string _mainBgHex = DefaultMainBg;

    /// <summary>终端窗口背景（框架区，非终端内容）。</summary>
    [ObservableProperty]
    private string _terminalBgHex = DefaultTerminalBg;

    /// <summary>波形窗口背景。</summary>
    [ObservableProperty]
    private string _chartBgHex = DefaultChartBg;

    /// <summary>模板编辑器背景。</summary>
    [ObservableProperty]
    private string _templateBgHex = DefaultTemplateBg;

    /// <summary>终端内容区底色（引擎默认背景；对端 OSC 11 仍可运行时改写）。</summary>
    [ObservableProperty]
    private string _termContentBgHex = DefaultTermContentBg;

    private Appearance() { }
}
