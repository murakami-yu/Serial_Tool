using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace SerialTool.App.Controls;

/// <summary>颜色选择控件：色块按钮弹出预设调色板，选中色写回 Hex（双向绑定到 VM）。</summary>
public partial class ColorChipButton : UserControl
{
    /// <summary>预设调色板 16 色：灰阶 + 常用色 + 主题蓝/正文色（含两个默认值）。</summary>
    public static readonly string[] Palette =
    {
        "#1E1E1E", "#5A5A5A", "#8A8A8A", "#B8B8B8",
        "#D9433B", "#E8833A", "#BF8F00", "#2E9E5B",
        "#00897B", "#0078D7", "#0F47AF", "#7030A0",
        "#C255A0", "#8B5A2B", "#2E5E4E", "#005F9E",
    };

    /// <summary>当前颜色（HEX 字符串，双向绑定 VM，改后 VM 触发全量重绘）。</summary>
    public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
        nameof(Hex), typeof(string), typeof(ColorChipButton),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>按钮标签（如 "发送色"）。</summary>
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ColorChipButton),
        new PropertyMetadata(string.Empty));

    /// <summary>「恢复默认」的目标色。</summary>
    public static readonly DependencyProperty DefaultHexProperty = DependencyProperty.Register(
        nameof(DefaultHex), typeof(string), typeof(ColorChipButton),
        new PropertyMetadata(string.Empty));

    public string Hex
    {
        get => (string)GetValue(HexProperty);
        set => SetValue(HexProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string DefaultHex
    {
        get => (string)GetValue(DefaultHexProperty);
        set => SetValue(DefaultHexProperty, value);
    }

    public ColorChipButton()
    {
        InitializeComponent();
    }

    private void Swatch_Click(object sender, RoutedEventArgs e)
    {
        Hex = (string)((Button)sender).CommandParameter!;
        Toggle.IsChecked = false; // 选完收弹层
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        Hex = DefaultHex;
        Toggle.IsChecked = false;
    }
}
