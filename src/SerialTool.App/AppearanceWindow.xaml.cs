using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using SerialTool.App.Services;

namespace SerialTool.App;

/// <summary>外观设置对话框：主题预设 + 各界面背景色 + 控件级配色集中管理（主窗状态栏「外观…」入口）。
/// 色块为纯绑定视图（ColorChipButton 双向绑定 Appearance 单例）；预设下拉由代码后置维护选中态——
/// 选中态不落盘，由当前 13 项色值反推匹配预设，任一单项微调破坏全等即自动跳「自定义」。</summary>
public partial class AppearanceWindow : Window
{
    private const string CustomLabel = "自定义";
    /// <summary>程序化改选中/应用预设期间置位，防 SelectionChanged/PropertyChanged 回环。</summary>
    private bool _applying;

    public AppearanceWindow()
    {
        InitializeComponent();
        foreach (var p in Appearance.Presets) PresetCombo.Items.Add(p.Name);
        PresetCombo.Items.Add(CustomLabel);
        RefreshPresetSelection();
        Appearance.Instance.PropertyChanged += OnAppearanceChanged;
        Closed += (_, _) => Appearance.Instance.PropertyChanged -= OnAppearanceChanged;
    }

    /// <summary>选中态 = 当前值匹配到的预设名，匹配不到显示「自定义」。</summary>
    private void RefreshPresetSelection()
    {
        if (_applying) return;
        _applying = true;
        PresetCombo.SelectedItem = Appearance.Instance.MatchPreset()?.Name ?? CustomLabel;
        _applying = false;
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e) => RefreshPresetSelection();

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying) return;
        if (PresetCombo.SelectedItem is not string name || name == CustomLabel) return;
        var preset = Appearance.Presets.FirstOrDefault(p => p.Name == name);
        if (preset is null) return;
        _applying = true;
        Appearance.Instance.ApplyPreset(preset); // 13 项逐属性写入：画刷替换/窗口背景/终端换底各自级联
        _applying = false;
        PresetCombo.SelectedItem = name; // 落定预设名（应用期间的 PropertyChanged 已被守卫跳过）
    }
}
