using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SerialTool.App.Services;
using XTerm;
using XTerm.Options;
using XCursorStyle = XTerm.Common.CursorStyle;
using XKey = XTerm.Input.Key;
using XMods = XTerm.Input.KeyModifiers;
using XMouseButton = XTerm.Input.MouseButton;
using XMouseEventType = XTerm.Input.MouseEventType;

namespace SerialTool.App.Controls;

/// <summary>
/// VT100/xterm 终端渲染视图：XTerm.NET 引擎（纯逻辑）的 WPF 自绘前端。
/// 线程模型：引擎实例只在 UI 线程触碰——任意线程 <see cref="EnqueueBytes"/> 入队，
/// 16ms UI 泵合帧喂 <c>terminal.Write</c> 并失效重绘（输出高峰每帧最多一次全视口重绘）。
/// 坐标语义（实证）：渲染行 r ↔ <c>Buffer.Lines[Buffer.ViewportY + r]</c>；
/// 游标视口行 = <c>YBase + Y - ViewportY</c>；<c>IsCellSelected(col, 视口行)</c> 内部自行加 ViewportY。
/// </summary>
public class TerminalView : FrameworkElement
{
    private const double FontSize = 14.0;
    private const double PadH = 3.0;   // 左右内边距（DIP）
    private const double PadV = 3.0;
    private const int ScrollWheelLines = 3;
    private const int ScrollKeyLines = 20;   // Shift+PgUp/PgDn 步进

    private readonly Terminal _terminal;
    private readonly ConcurrentQueue<byte[]> _inbox = new();
    private readonly DispatcherTimer _pump;    // 字节泵 + 重绘合帧
    private readonly DispatcherTimer _blink;   // 光标闪烁
    private bool _cursorOn = true;
    private double _pixelsPerDip = 1.0;

    private readonly FontFamily _font = new("Cascadia Mono, SimSun"); // VS Code 终端方案（2026-09-19 用户对比选定）：英文 Cascadia Mono（等宽，字距天然均匀），中文宋体回退（GB2312 字符集标准字体）
    private readonly Typeface _typeface;
    private readonly Typeface _typefaceBold;
    private readonly Typeface _typefaceItalic;
    private readonly Typeface _typefaceBoldItalic;
    private double _cellW = 8.0, _cellH = 18.0;  // 实测格宽/行高（DIP）
    private bool _metricsReady;

    private readonly Dictionary<int, SolidColorBrush> _brushCache = new();

    /// <summary>键入/粘贴/引擎回话（DA/DSR 应答）字节，UI 线程抛出 → 宿主转发后端。</summary>
    public event Action<byte[]>? InputEmitted;

    /// <summary>网格行列变化（窗口 resize 引起）→ 宿主通知后端（SSH 通道窗口变更；串口忽略）。</summary>
    public event Action<int, int>? Resized;

    /// <summary>视口移动/滚回总量变化（滚动、新输出）→ 宿主同步滚动条。</summary>
    public event Action? ViewportChanged;

    /// <summary>OSC 0/2 窗口标题变化。</summary>
    public event Action? TitleChanged;

    public Terminal Terminal => _terminal;

    public bool IsAltBuffer => _terminal.IsAlternateBufferActive;
    public int ViewportY => _terminal.Buffer.ViewportY;
    public int MaxViewportY => Math.Max(0, _terminal.Buffer.Length - _terminal.Buffer.Rows);
    public int ViewportRows => _terminal.Buffer.Rows;
    public bool HasSelection => _terminal.Selection.HasSelection;

    public TerminalView()
    {
        _terminal = new Terminal(new TerminalOptions
        {
            Cols = 80,
            Rows = 24,
            Scrollback = 5000,
            CursorBlink = true,
            Theme = BuildTheme(),
        });
        _terminal.DataReceived += (_, e) => InputEmitted?.Invoke(Encoding.UTF8.GetBytes(e.Data));
        _terminal.TitleChanged += (_, _) => TitleChanged?.Invoke();
        _terminal.Scrolled += (_, _) => { InvalidateVisual(); ViewportChanged?.Invoke(); };
        _terminal.Selection.SelectionChanged += () => InvalidateVisual();
        _terminal.Colors.ColorChanged += (_, _) => { _brushCache.Clear(); InvalidateVisual(); };

        _typeface = new Typeface(_font, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _typefaceBold = new Typeface(_font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _typefaceItalic = new Typeface(_font, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
        _typefaceBoldItalic = new Typeface(_font, FontStyles.Italic, FontWeights.Bold, FontStretches.Normal);

        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        SnapsToDevicePixels = true;

        ContextMenu = BuildContextMenu();

        _pump = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _pump.Tick += (_, _) => PumpBytes();

        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += (_, _) =>
        {
            if (IsKeyboardFocused && _terminal.CursorVisible)
            {
                _cursorOn = !_cursorOn;
                InvalidateVisual();
            }
            else _cursorOn = true;
        };

        Loaded += (_, _) => { _pump.Start(); _blink.Start(); InvalidateVisual(); };
        Unloaded += (_, _) => { _pump.Stop(); _blink.Stop(); };
        GotKeyboardFocus += (_, _) => InvalidateVisual();
        LostKeyboardFocus += (_, _) => InvalidateVisual();
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>任意线程调用：原始字节入队（读取线程 RX / 其他来源），UI 泵统一消费。</summary>
    public void EnqueueBytes(byte[] data)
    {
        if (data.Length == 0) return;
        _inbox.Enqueue(data);
    }

    public void Dispose()
    {
        _pump.Stop();
        _blink.Stop();
        _terminal.Dispose();
    }

    // ---------- 字节泵 ----------

    private void PumpBytes()
    {
        var any = false;
        while (_inbox.TryDequeue(out var bytes))
        {
            try { _terminal.Write(bytes); } catch { /* 引擎已释放等：丢弃后续 */ }
            any = true;
        }
        if (any)
        {
            InvalidateVisual();
            ViewportChanged?.Invoke();
        }
    }

    // ---------- 度量 ----------

    private void EnsureMetrics()
    {
        if (_metricsReady) return;
        // 格宽 = max(数字 "0" 字宽, CJK 全角/2)：Cascadia Mono 等宽字体单字符宽即格宽基准（"0"≈8.2 DIP @14pt），
        // 中文宋体全角 14 → 两格 16.4 留白仅 2.4。超格字符（如个别更宽字形/合成斜体溢出）
        // 仍由 DrawCell 水平压缩兜底（比例字体时期引入，保留作防御）。
        var ftDigit = MakeText("0", _typeface, Brushes.Black);
        var ftCjk = MakeText("中", _typeface, Brushes.Black);
        if (ftDigit.Width > 0 && ftCjk.Width > 0)
        {
            _cellW = Math.Max(ftDigit.Width, ftCjk.Width / 2);
            _cellH = Math.Ceiling(Math.Max(MakeText("W", _typefaceBold, Brushes.Black).Height, ftCjk.Height) * 1.08); // 行间微余量：终端观感 + 选区/下划线空间
        }
        _metricsReady = true;
    }

    private FormattedText MakeText(string text, Typeface face, Brush brush)
        => new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
               face, FontSize, brush, _pixelsPerDip);

    // ---------- 渲染 ----------

    protected override void OnRender(DrawingContext dc)
    {
        EnsureMetrics();
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11;
        if (m is > 0 && Math.Abs(m.Value - _pixelsPerDip) > 1e-9)
        {
            _pixelsPerDip = m.Value;
            _ftCache.Clear(); // FormattedText 与 pixelsPerDip 绑定，跨 DPI 屏拖窗后必须重建
        }

        var size = RenderSize;
        if (size.Width < 1 || size.Height < 1) return;

        // 网格尺寸随视口反推；变化时通知引擎重排（reflow）
        var cols = Math.Max(10, (int)((size.Width - PadH * 2) / _cellW));
        var rows = Math.Max(2, (int)((size.Height - PadV * 2) / _cellH));
        if (cols != _terminal.Cols || rows != _terminal.Rows)
        {
            _terminal.Resize(cols, rows);
            Resized?.Invoke(cols, rows);
            ViewportChanged?.Invoke();
        }

        var bgDef = ResolveColor(_terminal.Colors.Background, FallbackBg);
        dc.DrawRectangle(bgDef, null, new Rect(0, 0, size.Width, size.Height));

        var buf = _terminal.Buffer;
        var width = _terminal.Cols;

        for (var r = 0; r < _terminal.Rows; r++)
        {
            var lineIdx = buf.ViewportY + r;
            if (lineIdx < 0 || lineIdx >= buf.Length) continue;
            var line = buf.Lines[lineIdx];
            if (line is null) continue;
            var top = PadV + r * _cellH;
            RenderLine(dc, line, r, top, width);
        }

        RenderSelection(dc);
        RenderCursor(dc);
    }

    /// <summary>逐行渲染：TNR 是比例字体（字宽≠格宽），文字必须**逐格**定位绘制
    /// （同属性合并成一条 FormattedText 会让后续字符漂出格子）；背景仍按同底色连续格
    /// 合并为一个矩形（避免逐格矩形接缝露底）。宽字符（Width==2）在首格位绘制、占两格。</summary>
    private void RenderLine(DrawingContext dc, XTerm.Buffer.BufferLine line, int viewRow, double top, int width)
    {
        var length = Math.Min(line.Length, width);
        var col = 0;
        while (col < length)
        {
            var cell = line[col];
            if (cell.Width == 0) { col++; continue; } // 宽字符续格：由首格覆盖
            var cellW2 = Math.Max(1, cell.Width);

            var (fgVal, bgVal) = ResolveCellColors(cell.Attributes);

            // 背景段合并：同底色（含反显折算后）的连续格 → 一个矩形
            var bgEnd = col + cellW2;
            while (bgEnd < length)
            {
                var c2 = line[bgEnd];
                if (c2.Width == 0) { bgEnd++; continue; }
                var (_, b2) = ResolveCellColors(c2.Attributes);
                if (b2 != bgVal) break;
                bgEnd += Math.Max(1, c2.Width);
            }
            if (cell.Attributes.IsInverse() || bgVal != _terminal.Colors.Background)
                dc.DrawRectangle(ResolveColor(bgVal, FallbackBg), null,
                    new Rect(PadH + col * _cellW, top, (bgEnd - col) * _cellW, _cellH));

            // 文字：段内逐格绘制（缓存 FormattedText）
            var i = col;
            while (i < bgEnd)
            {
                var cc = line[i];
                if (cc.Width == 0) { i++; continue; }
                var cw = Math.Max(1, cc.Width);
                if (!string.IsNullOrEmpty(cc.Content))
                {
                    var hasText = false;
                    foreach (var ch in cc.Content) if (!char.IsWhiteSpace(ch)) { hasText = true; break; }
                    if (hasText) DrawCell(dc, cc.Content, cc.Attributes, i, top, cw);
                }
                i += cw;
            }
            col = bgEnd;
        }
    }

    /// <summary>单元格属性 → (前景, 背景) 0xRRGGBB（反显已折算）。</summary>
    private (int fg, int bg) ResolveCellColors(XTerm.Buffer.AttributeData attr)
    {
        var fg = ResolveAttrColor(attr.GetFgColorMode(), attr.GetFgColor(), _terminal.Colors.Foreground);
        var bg = ResolveAttrColor(attr.GetBgColorMode(), attr.GetBgColor(), _terminal.Colors.Background);
        if (attr.IsInverse()) (fg, bg) = (bg, fg);
        return (fg, bg);
    }

    // FormattedText 缓存：比例字体逐格绘制每帧构造量大，按 (文本,粗,斜,前景色) 缓存；
    // DPI 变化（跨屏拖窗）时整体失效（OnRender 里清）
    private readonly Dictionary<(string text, bool bold, bool italic, int fg), FormattedText> _ftCache = new();

    private FormattedText GetCellText(string text, bool bold, bool italic, int fg, Brush brush)
    {
        var key = (text, bold, italic, fg);
        if (_ftCache.TryGetValue(key, out var cached)) return cached;
        if (_ftCache.Count > 4000) _ftCache.Clear(); // 防御：长会话组合爆炸时从头再来
        var face = bold
            ? (italic ? _typefaceBoldItalic : _typefaceBold)
            : (italic ? _typefaceItalic : _typeface);
        var ft = MakeText(text, face, brush);
        _ftCache[key] = ft;
        return ft;
    }

    private void DrawCell(DrawingContext dc, string text, XTerm.Buffer.AttributeData attr,
        int col, double top, int cells)
    {
        var x = PadH + col * _cellW;
        var w = cells * _cellW;
        var (fgVal, _) = ResolveCellColors(attr);
        var fgBrush = ResolveColor(fgVal, FallbackFg);
        var bold = attr.IsBold();
        var italic = attr.IsItalic();
        var ft = GetCellText(text, bold, italic, fgVal, fgBrush);
        var y = top + (_cellH - ft.Height) / 2;

        // 比例字体格宽折中：比格宽的字符（TNR 的 M/W/m/w、粗体大写）水平压缩进格，
        // 以格左为原点（格内左对齐语义不变）；CJK 全角 14 ≤ 两格 18 不会触发
        var squeeze = ft.Width > w;
        var dim = attr.IsDim();
        if (dim) dc.PushOpacity(0.55);
        if (squeeze) dc.PushTransform(new ScaleTransform(w / ft.Width, 1.0, x, y));
        dc.DrawText(ft, new Point(x, y));

        // 下划线 / 删除线 / 上划线（Baseline 定位，按格宽不随字形压缩）
        var baseline = y + ft.Baseline;
        var pen = new Pen(fgBrush, 1.0);
        if (attr.IsUnderline())
            dc.DrawLine(pen, new Point(x, baseline + 1), new Point(x + w, baseline + 1));
        if (attr.IsStrikethrough())
            dc.DrawLine(pen, new Point(x, top + _cellH * 0.45), new Point(x + w, top + _cellH * 0.45));
        if (attr.IsOverline())
            dc.DrawLine(pen, new Point(x, top + 1), new Point(x + w, top + 1));
        if (squeeze) dc.Pop();
        if (dim) dc.Pop();
    }

    private void RenderSelection(DrawingContext dc)
    {
        if (!_terminal.Selection.HasSelection) return;
        var selBrush = new SolidColorBrush(Color.FromArgb(110, 38, 79, 120)); // 主题 Selection #264F78 半透明
        selBrush.Freeze();
        for (var r = 0; r < _terminal.Rows; r++)
        {
            var runStart = -1;
            for (var c = 0; c <= _terminal.Cols; c++)
            {
                // IsCellSelected 收视口相对行（内部加 ViewportY），实证见类注释
                var selected = c < _terminal.Cols && _terminal.Selection.IsCellSelected(c, r);
                if (selected && runStart < 0) runStart = c;
                if (!selected && runStart >= 0)
                {
                    dc.DrawRectangle(selBrush, null,
                        new Rect(PadH + runStart * _cellW, PadV + r * _cellH, (c - runStart) * _cellW, _cellH));
                    runStart = -1;
                }
            }
        }
    }

    private void RenderCursor(DrawingContext dc)
    {
        if (!_terminal.CursorVisible || !_cursorOn) return;
        var buf = _terminal.Buffer;
        var row = buf.YBase + buf.Y - buf.ViewportY;
        if (row < 0 || row >= _terminal.Rows || buf.X >= _terminal.Cols) return;

        var lineIdx = buf.ViewportY + row;
        var line = lineIdx >= 0 && lineIdx < buf.Length ? buf.Lines[lineIdx] : null;
        var cellWidth = 1;
        string? cellText = null;
        XTerm.Buffer.AttributeData cellAttr = default;
        if (line is not null && buf.X < line.Length)
        {
            var cell = line[buf.X];
            cellWidth = Math.Max(1, cell.Width);
            cellText = cell.Content;
            cellAttr = cell.Attributes;
        }

        var x = PadH + buf.X * _cellW;
        var y = PadV + row * _cellH;
        var w = cellWidth * _cellW;
        var cursorBrush = ResolveColor(_terminal.Colors.Cursor, FallbackFg);

        switch (_terminal.Options.CursorStyle)
        {
            case XCursorStyle.Underline:
                dc.DrawRectangle(cursorBrush, null, new Rect(x, y + _cellH - 2, w, 2));
                break;
            case XCursorStyle.Bar:
                dc.DrawRectangle(cursorBrush, null, new Rect(x, y, 2, _cellH));
                break;
            default: // Block：整格反色 + 原字符用底色重画
                dc.DrawRectangle(cursorBrush, null, new Rect(x, y, w, _cellH));
                if (!string.IsNullOrEmpty(cellText) && !string.IsNullOrEmpty(cellText.Trim()))
                {
                    // Block 光标格内字符用底色重画（含反显：取的是该格"底"一侧的颜色）
                    var underRgb = cellAttr.IsInverse()
                        ? ResolveAttrColor(cellAttr.GetFgColorMode(), cellAttr.GetFgColor(), _terminal.Colors.Foreground)
                        : ResolveAttrColor(cellAttr.GetBgColorMode(), cellAttr.GetBgColor(), _terminal.Colors.Background);
                    var fg = ResolveColor(underRgb, FallbackBg);
                    var ft = MakeText(cellText,
                        cellAttr.IsBold() ? _typefaceBold : _typeface, fg);
                    var ty = y + (_cellH - ft.Height) / 2;
                    // 与 DrawCell 同规则：超格宽字符水平压缩，避免光标块重画溢出
                    if (ft.Width > w)
                    {
                        dc.PushTransform(new ScaleTransform(w / ft.Width, 1.0, x, ty));
                        dc.DrawText(ft, new Point(x, ty));
                        dc.Pop();
                    }
                    else dc.DrawText(ft, new Point(x, ty));
                }
                break;
        }
    }

    // ---------- 颜色 ----------

    /// <summary>终端背景色（与主题 Background 一致；宿主边框、遮罩同步用此色）。
    /// 读外观设置单例（默认 #1E1E1E），每次访问取当前值——改色后新建/更新即用新值。</summary>
    public static Color ThemeBackground => ParseHexOrDefault(Appearance.Instance.TermContentBgHex);

    /// <summary>HEX → Color；非法值回退终端默认深底（手改配置文件兜底）。</summary>
    private static Color ParseHexOrDefault(string hex)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); }
        catch { return Color.FromRgb(0x1E, 0x1E, 0x1E); }
    }

    /// <summary>外观设置改终端底色时调用：引擎默认背景运行时可改（SetBackground 即 OSC 11 同路径），
    /// 画刷缓存整清 + 全量重绘（OnRender 每帧读 Colors.Background，无需其他干预）。</summary>
    public void SetContentBackground(Color c)
    {
        _terminal.Colors.SetBackground((c.R << 16) | (c.G << 8) | c.B);
        _brushCache.Clear();
        InvalidateVisual();
    }

    /// <summary>终端主题：Campbell 调色板（Windows Terminal 默认）+ 外观设置的内容底色
    /// （默认 VS Code 式柔和深底 #1E1E1E）。每实例新建——背景色取自外观设置单例，
    /// 静态共享会被「后建实例改色」牵连。仅引擎默认色——对端 OSC 10/11/104 仍可运行时改写。</summary>
    private static ThemeOptions BuildTheme() => new()
    {
        Background = Appearance.Instance.TermContentBgHex,
        Foreground = "#D4D4D4",
        Cursor = "#AEAFAD",
        Selection = "#264F78",
        Black = "#0C0C0C",
        Red = "#C50F1F",
        Green = "#13A10E",
        Yellow = "#C19C00",
        Blue = "#0037DA",
        Magenta = "#881798",
        Cyan = "#3A96DD",
        White = "#CCCCCC",
        BrightBlack = "#767676",
        BrightRed = "#E74856",
        BrightGreen = "#16C60C",
        BrightYellow = "#F9F1A5",
        BrightBlue = "#3B78FF",
        BrightMagenta = "#B4009E",
        BrightCyan = "#61D6D6",
        BrightWhite = "#F2F2F2",
    };

    private const int FallbackFg = 0xD4D4D4;
    private const int FallbackBg = 0x1E1E1E;

    /// <summary>0xRRGGBB → 冻结画刷（缓存）。OSC 104/10/11 改调色板时整缓存失效。</summary>
    private SolidColorBrush ResolveColor(int rgb, int fallback)
    {
        var key = rgb == 0 ? fallback : rgb;
        if (_brushCache.TryGetValue(key, out var cached)) return cached;
        var b = new SolidColorBrush(Color.FromRgb((byte)(key >> 16), (byte)(key >> 8), (byte)key));
        b.Freeze();
        _brushCache[key] = b;
        return b;
    }

    /// <summary>单元格属性颜色 → 0xRRGGBB。mode 0 = 256 色调色板索引（256=默认前景 / 257=默认背景标记），
    /// mode 1 = 真彩直出（实证：AttributeData.GetFgColorMode 返回 XTerm.Common.ColorMode 原值）。</summary>
    private int ResolveAttrColor(int mode, int val, int defaultRgb)
    {
        if (mode == 1) return val;                    // RGB
        if (val is 256 or 257) return defaultRgb;     // 默认色标记
        return PaletteColor(val);                     // 0-255 调色板
    }

    /// <summary>256 色索引 → 0xRRGGBB：0-15 取主题调色板（可被 OSC 改写），
    /// 16-255 按 xterm 标准 6×6×6 立方体 + 灰阶公式（标准值不受 OSC 影响，v1 取标称）。</summary>
    private int PaletteColor(int index)
    {
        var colors = _terminal.Colors;
        if (index is >= 0 and <= 15) return colors[index];
        if (index is >= 16 and <= 231)
        {
            var i = index - 16;
            Span<int> lv = stackalloc int[] { 0, 95, 135, 175, 215, 255 };
            return (lv[i / 36] << 16) | (lv[i / 6 % 6] << 8) | lv[i % 6];
        }
        if (index is >= 232 and <= 255)
        {
            var g = 8 + (index - 232) * 10;
            return (g << 16) | (g << 8) | g;
        }
        return FallbackFg;
    }

    // ---------- 键盘输入 ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = ToMods(Keyboard.Modifiers);

        // Shift+PgUp/PgDn：终端视口滚动（不发给应用）
        if (mods == XMods.Shift && key is Key.Prior or Key.Next)
        {
            e.Handled = true;
            ScrollViewportLines(key == Key.Prior ? -ScrollKeyLines : ScrollKeyLines);
            return;
        }

        // Ctrl+Shift+C/V 复制粘贴（终端标准键）；Ctrl+C 无选区时发 ^C（终端惯例），有选区时复制
        if (mods == (XMods.Control | XMods.Shift))
        {
            if (key == Key.C) { e.Handled = true; CopySelection(); return; }
            if (key == Key.V) { e.Handled = true; PasteFromClipboard(); return; }
        }
        if (key == Key.C && mods == XMods.Control && _terminal.Selection.HasSelection
            && !string.IsNullOrEmpty(_terminal.Selection.GetSelectionText()))
        {
            e.Handled = true;
            CopySelection();
            return;
        }
        // Ctrl+V 粘贴（Windows 用户习惯；原 \x16 literal next 极少使用）
        if (key == Key.V && mods == XMods.Control)
        {
            e.Handled = true;
            PasteFromClipboard();
            return;
        }

        // 特殊键 → 引擎编码
        if (MapKey(key) is { } xk)
        {
            e.Handled = true;
            Emit(_terminal.GenerateKeyInput(xk, mods));
            return;
        }

        // Ctrl/Alt + 字符键：字符路径合成控制序列（Ctrl+C=\x03 等）；Shift 决定大小写
        if ((mods & (XMods.Control | XMods.Alt)) != 0)
        {
            var c = CharFromKey(key, (mods & XMods.Shift) != 0);
            if (c.HasValue)
            {
                e.Handled = true;
                Emit(_terminal.GenerateCharInput(c.Value, mods));
                return;
            }
        }
        // 其余留给 TextInput（可打印字符 / IME 提交）
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;
        e.Handled = true;
        var mods = ToMods(Keyboard.Modifiers);
        foreach (var ch in e.Text)
            Emit(_terminal.GenerateCharInput(ch, mods));
    }

    private void Emit(string seq)
    {
        if (string.IsNullOrEmpty(seq)) return;
        InputEmitted?.Invoke(Encoding.UTF8.GetBytes(seq));
    }

    private static XMods ToMods(ModifierKeys m)
    {
        var r = XMods.None;
        if (m.HasFlag(ModifierKeys.Shift)) r |= XMods.Shift;
        if (m.HasFlag(ModifierKeys.Control)) r |= XMods.Control;
        if (m.HasFlag(ModifierKeys.Alt)) r |= XMods.Alt;
        return r;
    }

    /// <summary>WPF 键 → 引擎特殊键；null = 非特殊键（数字小键盘 NumLock 开时走 TextInput 数字）。</summary>
    private static XKey? MapKey(Key key) => key switch
    {
        Key.Enter or Key.Return => XKey.Enter,
        Key.Tab => XKey.Tab,
        Key.Back => XKey.Backspace,
        Key.Escape => XKey.Escape,
        Key.Up => XKey.UpArrow,
        Key.Down => XKey.DownArrow,
        Key.Left => XKey.LeftArrow,
        Key.Right => XKey.RightArrow,
        Key.Home => XKey.Home,
        Key.End => XKey.End,
        Key.Prior => XKey.PageUp,
        Key.Next => XKey.PageDown,
        Key.Insert => XKey.Insert,
        Key.Delete => XKey.Delete,
        Key.F1 => XKey.F1,
        Key.F2 => XKey.F2,
        Key.F3 => XKey.F3,
        Key.F4 => XKey.F4,
        Key.F5 => XKey.F5,
        Key.F6 => XKey.F6,
        Key.F7 => XKey.F7,
        Key.F8 => XKey.F8,
        Key.F9 => XKey.F9,
        Key.F10 => XKey.F10,
        Key.F11 => XKey.F11,
        Key.F12 => XKey.F12,
        Key.Decimal when !IsNumLockOn() => XKey.KeypadDecimal,
        Key.Add when !IsNumLockOn() => XKey.KeypadAdd,
        Key.Subtract when !IsNumLockOn() => XKey.KeypadSubtract,
        Key.Multiply when !IsNumLockOn() => XKey.KeypadMultiply,
        Key.Divide when !IsNumLockOn() => XKey.KeypadDivide,
        Key.NumPad0 when !IsNumLockOn() => XKey.Keypad0,
        Key.NumPad1 when !IsNumLockOn() => XKey.Keypad1,
        Key.NumPad2 when !IsNumLockOn() => XKey.Keypad2,
        Key.NumPad3 when !IsNumLockOn() => XKey.Keypad3,
        Key.NumPad4 when !IsNumLockOn() => XKey.Keypad4,
        Key.NumPad5 when !IsNumLockOn() => XKey.Keypad5,
        Key.NumPad6 when !IsNumLockOn() => XKey.Keypad6,
        Key.NumPad7 when !IsNumLockOn() => XKey.Keypad7,
        Key.NumPad8 when !IsNumLockOn() => XKey.Keypad8,
        Key.NumPad9 when !IsNumLockOn() => XKey.Keypad9,
        _ => null,
    };

    private static bool IsNumLockOn()
        => Keyboard.IsKeyToggled(Key.NumLock);

    private static char? CharFromKey(Key key, bool shift)
        => key switch
        {
            >= Key.A and <= Key.Z => (char)((shift ? 'A' : 'a') + (key - Key.A)),
            >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
            Key.Space => ' ',
            Key.OemMinus => shift ? '_' : '-',
            Key.OemOpenBrackets => shift ? '{' : '[',
            Key.OemCloseBrackets => shift ? '}' : ']',
            Key.OemSemicolon => shift ? ':' : ';',
            Key.OemQuotes => shift ? '"' : '\'',
            Key.OemComma => shift ? '<' : ',',
            Key.OemPeriod => shift ? '>' : '.',
            Key.OemQuestion => shift ? '?' : '/',
            Key.OemPipe => shift ? '|' : '\\',
            Key.OemTilde => shift ? '~' : '`',
            Key.OemPlus => shift ? '+' : '=',
            _ => null,
        };

    // ---------- 鼠标 ----------

    private Point? _selAnchor;
    private bool _forceSelecting;   // Shift 强制选择中（绕过应用鼠标跟踪）
    private bool _mouseDownForwarded; // 左键按下已作为鼠标事件转发（跟踪模式下抬起也要转发）

    private (int col, int row) CellFromPoint(Point p)
    {
        var col = (int)((p.X - PadH) / _cellW);
        var row = (int)((p.Y - PadV) / _cellH);
        return (Math.Clamp(col, 0, _terminal.Cols - 1), Math.Clamp(row, 0, _terminal.Rows - 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var (col, row) = CellFromPoint(e.GetPosition(this));
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Shift + 左键：强制进入文字选择（绕过应用鼠标跟踪）——TUI 应用里也能选字复制
        if (shift)
        {
            _forceSelecting = true;
            _mouseDownForwarded = false;
            Mouse.Capture(this);
            _selAnchor = new Point(col, row);
            _terminal.Selection.StartSelection(col, row, XTerm.Selection.SelectionMode.Normal);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // 应用开启鼠标跟踪：左键按下作为鼠标事件转发
        if (_terminal.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None)
        {
            _mouseDownForwarded = true;
            Emit(_terminal.GenerateMouseEvent(
                XMouseButton.Left, col, row, XMouseEventType.Down, ToMods(Keyboard.Modifiers)));
            e.Handled = true;
            return;
        }

        // 普通选择
        _forceSelecting = false;
        _mouseDownForwarded = false;
        Mouse.Capture(this);
        _selAnchor = new Point(col, row);
        _terminal.Selection.StartSelection(col, row, XTerm.Selection.SelectionMode.Normal);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var (col, row) = CellFromPoint(e.GetPosition(this));

        // 强制选择中：始终更新选区
        if (_forceSelecting && _selAnchor.HasValue && e.LeftButton == MouseButtonState.Pressed)
        {
            _terminal.Selection.UpdateSelection(col, row);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // 鼠标跟踪模式 + 左键拖动：作为 Drag 事件转发
        if (_mouseDownForwarded && e.LeftButton == MouseButtonState.Pressed
            && _terminal.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None)
        {
            if (_terminal.MouseTrackingMode == XTerm.Input.MouseTrackingMode.AnyEvent
                || _terminal.MouseTrackingMode == XTerm.Input.MouseTrackingMode.ButtonEvent)
            {
                Emit(_terminal.GenerateMouseEvent(
                    XMouseButton.Left, col, row, XMouseEventType.Drag, ToMods(Keyboard.Modifiers)));
            }
            e.Handled = true;
            return;
        }

        // 普通选区拖动
        if (_selAnchor is null || e.LeftButton != MouseButtonState.Pressed) return;
        _terminal.Selection.UpdateSelection(col, row);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        var (col, row) = CellFromPoint(e.GetPosition(this));

        // 鼠标跟踪模式抬起：转发 Up 事件
        if (_mouseDownForwarded)
        {
            _mouseDownForwarded = false;
            if (_terminal.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None)
            {
                Emit(_terminal.GenerateMouseEvent(
                    XMouseButton.Left, col, row, XMouseEventType.Up, ToMods(Keyboard.Modifiers)));
            }
            e.Handled = true;
            return;
        }

        if (_selAnchor is null) { e.Handled = true; return; }
        var anchor = _selAnchor.Value;
        Mouse.Capture(null);
        _selAnchor = null;
        _forceSelecting = false;
        _terminal.Selection.EndSelection();
        // 纯点击（未拖动）留下的是零宽选区，引擎 EndSelection 不会自动清除（HasSelection 恒真，
        // 会把 Ctrl+C 导向复制分支吞掉 ^C）——零宽即清除，顺带实现"点击清除已有选区"的终端惯例
        if (col == (int)anchor.X && row == (int)anchor.Y)
            _terminal.Selection.ClearSelection();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // Shift + 右键：强制弹上下文菜单（绕过鼠标跟踪）
        // 无 Shift 且应用开鼠标跟踪：右键转发给应用
        if (!shift && _terminal.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None)
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            Emit(_terminal.GenerateMouseEvent(
                XMouseButton.Right, col, row, XMouseEventType.Down, ToMods(Keyboard.Modifiers)));
            e.Handled = true;
            return;
        }

        // 普通 / Shift 强制：上下文菜单由 WPF 原生弹出（ContextMenu 已在构造函数绑定）
        // 不设 e.Handled = true，让 WPF 继续冒泡触发 ContextMenuOpening
        base.OnMouseRightButtonDown(e);
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        if (_terminal.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None
            && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var (col, row) = CellFromPoint(e.GetPosition(this));
            Emit(_terminal.GenerateMouseEvent(
                XMouseButton.Right, col, row, XMouseEventType.Up, ToMods(Keyboard.Modifiers)));
            e.Handled = true;
            return;
        }
        base.OnMouseRightButtonUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var (col, row) = CellFromPoint(e.GetPosition(this));
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var linesPerWheel = 3;  // 每滚轮格（Delta=120）模拟 3 行，对齐 Windows Terminal 默认
        var steps = Math.Abs(e.Delta) / 120;
        if (steps < 1) steps = 1;
        var up = e.Delta > 0;

        // Shift + 滚轮：始终由终端自己处理（绕过鼠标跟踪）
        //   - 非备屏：滚回滚缓冲区（滚轮版 Shift+PgUp/PgDn，速度与普通滚回一致）
        //   - 备屏：模拟 PageUp/PageDown 发给应用（整页翻动，适合看大段内容）
        if (shift)
        {
            if (_terminal.IsAlternateBufferActive)
            {
                var key = up ? XKey.PageUp : XKey.PageDown;
                for (var i = 0; i < steps; i++)
                    Emit(_terminal.GenerateKeyInput(key, XMods.None));
            }
            else
            {
                ScrollViewportLines(up ? -ScrollWheelLines * steps : ScrollWheelLines * steps);
            }
            return;
        }

        // 应用开启鼠标跟踪（vim/htop 等）：滚轮作为鼠标事件转发
        if (_terminal.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None)
        {
            var ev = up ? XMouseEventType.WheelUp : XMouseEventType.WheelDown;
            var btn = up ? XMouseButton.WheelUp : XMouseButton.WheelDown;
            // 滚轮多分格：每格发一次
            for (var i = 0; i < steps; i++)
                Emit(_terminal.GenerateMouseEvent(btn, col, row, ev, ToMods(Keyboard.Modifiers)));
            return;
        }

        // 备屏 + 无鼠标跟踪（less/man/vim 鼠标关 等）：模拟 Up/Down 按键
        if (_terminal.IsAlternateBufferActive)
        {
            var key = up ? XKey.UpArrow : XKey.DownArrow;
            var n = linesPerWheel * steps;
            for (var i = 0; i < n; i++)
                Emit(_terminal.GenerateKeyInput(key, XMods.None));
            return;
        }

        // 普通缓冲区：滚回滚
        ScrollViewportLines(up ? -ScrollWheelLines * steps : ScrollWheelLines * steps);
    }

    // ---------- 滚动 / 公共操作 ----------

    private void ScrollViewportLines(int lines)
    {
        _terminal.Buffer.ScrollLines(lines);
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    /// <summary>滚动到指定视口顶部行（滚动条驱动；引擎在底部时新输出自动跟随，滚上去则冻结查看）。</summary>
    public void ScrollViewport(int topLine)
    {
        _terminal.Buffer.ViewportY = Math.Clamp(topLine, 0, MaxViewportY);
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    public void ScrollToBottom() => ScrollViewportLines(int.MaxValue / 4); // ScrollLines 钳制到底

    public void CopySelection()
    {
        var text = _terminal.Selection.GetSelectionText();
        if (!string.IsNullOrEmpty(text))
            Clipboard.SetText(text);
        // 复制后清除选区（Windows Terminal / xterm.js 惯例）：引擎不会因新输出自动清选区，
        // 留着会让后续 Ctrl+C 永远进复制分支、再也发不出 ^C（实测 GetSelectionText 对零宽
        // 选区也返回非空字符，光靠"空文本"判断挡不住）
        _terminal.Selection.ClearSelection();
        InvalidateVisual();
    }

    public void PasteFromClipboard()
    {
        if (Clipboard.ContainsText())
            _terminal.Paste(Clipboard.GetText()); // 括号粘贴模式由引擎处理，经 DataReceived → InputEmitted 发后端
    }

    public void ClearScreen()
    {
        _terminal.Write("\x1b[H\x1b[2J\x1b[3J"); // 光标归位 + 清屏 + 清滚回
        _terminal.Buffer.ClearScrollback();
        InvalidateVisual();
        ViewportChanged?.Invoke();
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "复制(_C)" };
        copy.Click += (_, _) => CopySelection();
        var paste = new MenuItem { Header = "粘贴(_P)" };
        paste.Click += (_, _) => PasteFromClipboard();
        var clear = new MenuItem { Header = "清屏(_L)" };
        clear.Click += (_, _) => ClearScreen();
        var bottom = new MenuItem { Header = "回到底部(_B)" };
        bottom.Click += (_, _) => ScrollToBottom();
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(clear);
        menu.Items.Add(bottom);
        menu.Opened += (_, _) => copy.IsEnabled = _terminal.Selection.HasSelection;
        return menu;
    }
}
