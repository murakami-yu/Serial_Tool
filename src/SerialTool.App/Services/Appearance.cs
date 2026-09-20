using CommunityToolkit.Mvvm.ComponentModel;

namespace SerialTool.App.Services;

/// <summary>外观设置单例：各窗口背景色 + 控件级配色（HEX 字符串），x:Static 绑定源。
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

    // ---------- 控件级配色（全局，改 App.xaml 资源画刷实时级联；边框色派生控件边框/分隔线） ----------

    public const string DefaultPanel = "#FFFFFF";       // 面板/输入框/下拉框/勾选框底色
    public const string DefaultButtonBg = "#F0F0F0";    // 按钮底色
    public const string DefaultBorder = "#C9C9C9";      // 边框色（控件边框加深派生 #ADADAD、分隔线调亮派生 #E2E2E2）
    public const string DefaultText = "#1E1E1E";        // 正文文字
    public const string DefaultMuted = "#5A5A5A";       // 次要文字
    public const string DefaultAccent = "#0078D7";      // 强调色（聚焦边/勾选勾底）
    public const string DefaultHover = "#CCE4F7";       // 悬停高亮
    public const string DefaultSelected = "#B3D4F0";    // 选中/按下/文本选择

    /// <summary>面板/输入框底色（PanelBrush）。</summary>
    [ObservableProperty]
    private string _panelHex = DefaultPanel;

    /// <summary>按钮底色（ButtonBgBrush）。</summary>
    [ObservableProperty]
    private string _buttonBgHex = DefaultButtonBg;

    /// <summary>边框色（BorderBrush；控件边框/分隔线/滚动条滑块由此派生）。</summary>
    [ObservableProperty]
    private string _borderHex = DefaultBorder;

    /// <summary>正文文字（TextBrush）。</summary>
    [ObservableProperty]
    private string _textHex = DefaultText;

    /// <summary>次要文字（MutedBrush）。</summary>
    [ObservableProperty]
    private string _mutedHex = DefaultMuted;

    /// <summary>强调色（AccentBrush）。</summary>
    [ObservableProperty]
    private string _accentHex = DefaultAccent;

    /// <summary>悬停高亮（HoverBrush）。</summary>
    [ObservableProperty]
    private string _hoverHex = DefaultHover;

    /// <summary>选中/按下/文本选择（SelectedBrush）。</summary>
    [ObservableProperty]
    private string _selectedHex = DefaultSelected;

    private Appearance() { }

    // ---------- 预设主题（整套 13 项打包；选中态不落盘，由 MatchPreset 按当前值反推） ----------

    /// <summary>内置预设主题。经典浅色引用默认色常量（单一事实源）。</summary>
    public static AppearancePreset[] Presets { get; } =
    {
        new("经典浅色", DefaultMainBg, DefaultTerminalBg, DefaultChartBg, DefaultTemplateBg, DefaultTermContentBg,
            DefaultPanel, DefaultButtonBg, DefaultBorder, DefaultText, DefaultMuted, DefaultAccent,
            DefaultHover, DefaultSelected),
        new("深色夜间", "#2B2B2B", "#2B2B2B", "#2B2B2B", "#2B2B2B", "#1E1E1E",
            "#2D2D30", "#3E3E42", "#3F3F46", "#E8E8E8", "#9D9DA3", "#0078D7", "#3A4756", "#264F78"),
        new("护眼绿", "#E8F3E8", "#E8F3E8", "#E8F3E8", "#E8F3E8", "#1E1E1E",
            "#F2FAF2", "#DCEDDC", "#9CC5A0", "#1E3A1E", "#5A7A5A", "#2E9E5B", "#D0EBD0", "#B8E0B8"),
        new("高对比", "#000000", "#000000", "#000000", "#000000", "#000000",
            "#000000", "#1A1A1A", "#FFFFFF", "#FFFFFF", "#C0C0C0", "#0090FF", "#1F3A52", "#005A9E"),
    };

    /// <summary>整套应用预设：逐属性写入（相等值源生成器自动跳过通知），各订阅方实时级联。</summary>
    public void ApplyPreset(AppearancePreset p)
    {
        MainBgHex = p.MainBg; TerminalBgHex = p.TerminalBg; ChartBgHex = p.ChartBg;
        TemplateBgHex = p.TemplateBg; TermContentBgHex = p.TermContentBg;
        PanelHex = p.Panel; ButtonBgHex = p.ButtonBg; BorderHex = p.Border;
        TextHex = p.Text; MutedHex = p.Muted; AccentHex = p.Accent;
        HoverHex = p.Hover; SelectedHex = p.Selected;
    }

    /// <summary>当前 13 值与某预设全等（大小写不敏感）则返回该预设，否则 null（= 自定义）。</summary>
    public AppearancePreset? MatchPreset()
    {
        foreach (var p in Presets)
            if (Eq(MainBgHex, p.MainBg) && Eq(TerminalBgHex, p.TerminalBg) && Eq(ChartBgHex, p.ChartBg) &&
                Eq(TemplateBgHex, p.TemplateBg) && Eq(TermContentBgHex, p.TermContentBg) &&
                Eq(PanelHex, p.Panel) && Eq(ButtonBgHex, p.ButtonBg) && Eq(BorderHex, p.Border) &&
                Eq(TextHex, p.Text) && Eq(MutedHex, p.Muted) && Eq(AccentHex, p.Accent) &&
                Eq(HoverHex, p.Hover) && Eq(SelectedHex, p.Selected))
                return p;
        return null;

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>外观预设主题：13 项色值整套打包（4 窗口背景 + 终端内容底色 + 8 控件色）。</summary>
public sealed record AppearancePreset(
    string Name,
    string MainBg, string TerminalBg, string ChartBg, string TemplateBg, string TermContentBg,
    string Panel, string ButtonBg, string Border, string Text, string Muted, string Accent,
    string Hover, string Selected);
