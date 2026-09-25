using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SerialTool.App.Controls;

/// <summary>颜色选择控件：色块按钮弹出取色板（HSV 色图 + 预设色 + HEX/RGB 输入），选中色写回 Hex（双向绑定到 VM）。</summary>
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

    /// <summary>是否在按钮右端显示当前 HEX 色值（外观设置等宽布局用；默认关，主窗发送色/接收色保持紧凑）。</summary>
    public static readonly DependencyProperty ShowHexProperty = DependencyProperty.Register(
        nameof(ShowHex), typeof(bool), typeof(ColorChipButton),
        new PropertyMetadata(false));

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

    public bool ShowHex
    {
        get => (bool)GetValue(ShowHexProperty);
        set => SetValue(ShowHexProperty, value);
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

    // ---------- 自定义色输入（HEX ⇄ RGB 双向联动，应用即写回 Hex 实时生效） ----------

    /// <summary>联动更新期间置位，防 HEX→RGB→HEX 回环。</summary>
    private bool _syncing;

    /// <summary>弹层每次打开按当前 Hex 重置输入区（预设/微调可能已在外部改色）。</summary>
    private void PalettePopup_Opened(object? sender, System.EventArgs e)
    {
        SyncFromHex(Hex);
        // 色图十字圈/色相标跟随当前色；ActualWidth 要等弹层布局完成才有效，推迟到布局后执行
        if (TryParseHex(Hex, out var c))
            Dispatcher.BeginInvoke(new Action(() => SyncMapFromColor(c)));
    }

    // ---------- 色图取色（HSV）：色图定饱和度/明度，色相条定色相；拖动只刷预览，松手即应用 ----------

    /// <summary>色相 0-360、饱和度/明度 0-1（色图 + 色相条的当前状态）。</summary>
    private double _hue, _sat = 1, _val = 1;
    private bool _mapDrag, _hueDrag;

    private void Map_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _mapDrag = true;
        ColorMap.CaptureMouse();   // 按住拖出图外仍能持续取色，松手才释放
        MapPick(e.GetPosition(ColorMap));
    }

    private void Map_MouseMove(object sender, MouseEventArgs e)
    {
        if (_mapDrag) MapPick(e.GetPosition(ColorMap));
    }

    private void Map_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_mapDrag) return;
        _mapDrag = false;
        ColorMap.ReleaseMouseCapture();
        MapPick(e.GetPosition(ColorMap));
        ApplyCustom();   // 松手即写回 Hex（等同「应用」按钮）
    }

    private void MapPick(Point p)
    {
        double w = ColorMap.ActualWidth, h = ColorMap.ActualHeight;
        if (w <= 0 || h <= 0) return;
        _sat = Clamp01(p.X / w);
        _val = 1 - Clamp01(p.Y / h);
        ApplyHsv();
    }

    private void Hue_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _hueDrag = true;
        HueBar.CaptureMouse();
        HuePick(e.GetPosition(HueBar));
    }

    private void Hue_MouseMove(object sender, MouseEventArgs e)
    {
        if (_hueDrag) HuePick(e.GetPosition(HueBar));
    }

    private void Hue_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_hueDrag) return;
        _hueDrag = false;
        HueBar.ReleaseMouseCapture();
        HuePick(e.GetPosition(HueBar));
        ApplyCustom();
    }

    private void HuePick(Point p)
    {
        double w = HueBar.ActualWidth;
        if (w <= 0) return;
        _hue = Clamp01(p.X / w) * 360.0;
        ApplyHsv();
    }

    /// <summary>按 _hue/_sat/_val 重算颜色：刷预览与 HEX/RGB 输入框（走 _syncing 防回环）、摆十字圈与色相标。</summary>
    private void ApplyHsv()
    {
        var c = HsvToRgb(_hue, _sat, _val);
        HueStop.Color = HsvToRgb(_hue, 1, 1);
        _syncing = true;
        try
        {
            HexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            RBox.Text = c.R.ToString();
            GBox.Text = c.G.ToString();
            BBox.Text = c.B.ToString();
            PreviewBox.Background = new SolidColorBrush(c);
        }
        finally { _syncing = false; }
        MapThumb.Fill = new SolidColorBrush(c);
        PositionThumbs();
    }

    /// <summary>按既有颜色摆放色图（不改输入框——HSV 往返换算有 ±1 舍入漂移，展示值以 Hex 真值为准）。</summary>
    private void SyncMapFromColor(Color c)
    {
        RgbToHsv(c, out var h, out var s, out var v);
        if (s > 0.01) _hue = h;   // 灰阶色不带色相信息，沿用上次色相，色图底色不跳红
        _sat = s;
        _val = v;
        HueStop.Color = HsvToRgb(_hue, 1, 1);
        MapThumb.Fill = new SolidColorBrush(c);
        PositionThumbs();
    }

    private void PositionThumbs()
    {
        double mw = ColorMap.ActualWidth, mh = ColorMap.ActualHeight, hw = HueBar.ActualWidth;
        if (mw > 0 && mh > 0)
        {
            Canvas.SetLeft(MapThumb, _sat * mw - 6);
            Canvas.SetTop(MapThumb, (1 - _val) * mh - 6);
        }
        if (hw > 0) Canvas.SetLeft(HueThumb, _hue / 360.0 * hw - 3);
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = (h % 360 + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private static void RgbToHsv(Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        v = max;
        s = max == 0 ? 0 : d / max;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * ((g - b) / d % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
    }

    private void SyncFromHex(string hex)
    {
        _syncing = true;
        try
        {
            HexBox.Text = hex?.TrimStart('#') ?? "";
            if (TryParseHex(hex, out var c))
            {
                RBox.Text = c.R.ToString();
                GBox.Text = c.G.ToString();
                BBox.Text = c.B.ToString();
                PreviewBox.Background = new SolidColorBrush(c);
            }
        }
        finally { _syncing = false; }
    }

    /// <summary>接受 RRGGBB 或 #RRGGBB（大小写不敏感）。</summary>
    private static bool TryParseHex(string? s, out Color c)
    {
        c = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, null, out var v)) return false;
        c = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    private void HexBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!TryParseHex(HexBox.Text, out var c)) return;
        _syncing = true;
        try
        {
            RBox.Text = c.R.ToString();
            GBox.Text = c.G.ToString();
            BBox.Text = c.B.ToString();
            PreviewBox.Background = new SolidColorBrush(c);
        }
        finally { _syncing = false; }
        SyncMapFromColor(c);   // 手输精确色，色图十字圈同步跟随
    }

    private void RgbBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (!TryReadChannel(RBox.Text, out var r) || !TryReadChannel(GBox.Text, out var g) ||
            !TryReadChannel(BBox.Text, out var b)) return;
        var c = Color.FromRgb(r, g, b);
        _syncing = true;
        try
        {
            HexBox.Text = $"{c.R:X2}{c.G:X2}{c.B:X2}";
            PreviewBox.Background = new SolidColorBrush(c);
        }
        finally { _syncing = false; }
        SyncMapFromColor(c);
    }

    private static bool TryReadChannel(string? s, out byte v)
        => byte.TryParse(s, out v);   // byte 上限 255，天然截断超界

    /// <summary>应用自定义色（保持弹层打开，可继续微调）；HEX 框内 Enter 等同。</summary>
    private void ApplyCustom_Click(object sender, RoutedEventArgs e) => ApplyCustom();

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyCustom(); e.Handled = true; }
    }

    private void ApplyCustom()
    {
        if (TryParseHex(HexBox.Text, out var c))
            Hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
    }
}
