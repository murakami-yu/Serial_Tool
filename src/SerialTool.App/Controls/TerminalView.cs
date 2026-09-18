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

    private readonly FontFamily _font = new("Global Monospace"); // 复合字体：CJK 等宽回退、宽度=2 格
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
        var m = _typeface;
        var ft = MakeText("M", m, Brushes.Black);
        if (ft.Width > 0 && ft.Height > 0)
        {
            _cellW = ft.Width;
            _cellH = Math.Ceiling(ft.Height * 1.08); // 行间微余量：终端观感 + 选区/下划线空间
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
        if (m is > 0) _pixelsPerDip = m.Value;

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

    /// <summary>逐行渲染：Width==1 且属性相同的连续格合并为一条 FormattedText；
    /// 宽字符（Width==2）独立成段，按格位定位（避免 CJK 回退字体实际字宽 ≠ 2 格时后续字符漂移）。</summary>
    private void RenderLine(DrawingContext dc, XTerm.Buffer.BufferLine line, int viewRow, double top, int width)
    {
        var col = 0;
        var length = Math.Min(line.Length, width);
        while (col < length)
        {
            var cell = line[col];
            if (cell.Width == 0 || string.IsNullOrEmpty(cell.Content)) { col++; continue; }

            if (cell.Width >= 2)
            {
                DrawRun(dc, line, viewRow, top, col, cell.Content, cell.Attributes, cells: cell.Width);
                col += cell.Width;
                continue;
            }

            var runStart = col;
            var runAttr = cell.Attributes;
            var sb = new StringBuilder(32);
            while (col < length)
            {
                var c = line[col];
                if (c.Width != 1 || string.IsNullOrEmpty(c.Content) || !c.Attributes.Equals(runAttr)) break;
                sb.Append(c.Content);
                col++;
            }
            DrawRun(dc, line, viewRow, top, runStart, sb.ToString(), runAttr, cells: col - runStart);
        }
    }

    private void DrawRun(DrawingContext dc, XTerm.Buffer.BufferLine line, int viewRow, double top,
        int col, string text, XTerm.Buffer.AttributeData attr, int cells)
    {
        var x = PadH + col * _cellW;
        var w = cells * _cellW;

        var fgMode = attr.GetFgColorMode();
        var fgColor = attr.GetFgColor();
        var bgMode = attr.GetBgColorMode();
        var bgColor = attr.GetBgColor();
        var inverse = attr.IsInverse();

        int fgVal, bgVal;
        if (inverse)
        {
            fgVal = bgMode == 0 ? _terminal.Colors.Background : bgColor;
            bgVal = fgMode == 0 ? _terminal.Colors.Foreground : fgColor;
        }
        else
        {
            fgVal = fgMode == 0 ? _terminal.Colors.Foreground : fgColor;
            bgVal = bgMode == 0 ? _terminal.Colors.Background : bgColor;
        }

        // 背景：非默认或反显时绘制（默认背景已由整幅底色覆盖）
        if (inverse || bgMode != 0)
            dc.DrawRectangle(ResolveColor(bgVal, FallbackBg), null, new Rect(x, top, w, _cellH));

        var hasText = false;
        foreach (var ch in text) if (!char.IsWhiteSpace(ch)) { hasText = true; break; }
        if (!hasText) return;

        var bold = attr.IsBold();
        var italic = attr.IsItalic();
        var face = bold
            ? (italic ? _typefaceBoldItalic : _typefaceBold)
            : (italic ? _typefaceItalic : _typeface);
        var fgBrush = ResolveColor(fgVal, FallbackFg);

        var dim = attr.IsDim();
        if (dim) dc.PushOpacity(0.55);
        var ft = MakeText(text, face, fgBrush);
        var y = top + (_cellH - ft.Height) / 2;
        dc.DrawText(ft, new Point(x, y));

        // 下划线 / 删除线 / 上划线（Baseline 定位）
        var baseline = y + ft.Baseline;
        var pen = new Pen(fgBrush, 1.0);
        if (attr.IsUnderline())
            dc.DrawLine(pen, new Point(x, baseline + 1), new Point(x + w, baseline + 1));
        if (attr.IsStrikethrough())
            dc.DrawLine(pen, new Point(x, top + _cellH * 0.45), new Point(x + w, top + _cellH * 0.45));
        if (attr.IsOverline())
            dc.DrawLine(pen, new Point(x, top + 1), new Point(x + w, top + 1));
        if (dim) dc.Pop();
    }

    private void RenderSelection(DrawingContext dc)
    {
        if (!_terminal.Selection.HasSelection) return;
        var selBrush = new SolidColorBrush(Color.FromArgb(90, 0, 120, 215));
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
                    var fg = cellAttr.IsInverse()
                        ? ResolveColor(cellAttr.GetBgColorMode() == 0 ? _terminal.Colors.Background : cellAttr.GetBgColor(), FallbackBg)
                        : ResolveColor(cellAttr.GetFgColorMode() == 0 ? _terminal.Colors.Background : cellAttr.GetFgColor(), FallbackBg);
                    var ft = MakeText(cellText,
                        cellAttr.IsBold() ? _typefaceBold : _typeface, fg);
                    dc.DrawText(ft, new Point(x, y + (_cellH - ft.Height) / 2));
                }
                break;
        }
    }

    // ---------- 颜色 ----------

    private const int FallbackFg = 0xFFFFFF;
    private const int FallbackBg = 0x000000;

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

        // Ctrl+Shift+C/V 复制粘贴；Ctrl+C 无选区时发 ^C（终端惯例），有选区时复制
        if (mods == (XMods.Control | XMods.Shift))
        {
            if (key == Key.C) { e.Handled = true; CopySelection(); return; }
            if (key == Key.V) { e.Handled = true; PasteFromClipboard(); return; }
        }
        if (key == Key.C && mods == XMods.Control && _terminal.Selection.HasSelection)
        {
            e.Handled = true;
            CopySelection();
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

    private (int col, int row) CellFromPoint(Point p)
    {
        var col = (int)((p.X - PadH) / _cellW);
        var row = (int)((p.Y - PadV) / _cellH);
        return (Math.Clamp(col, 0, _terminal.Cols - 1), Math.Clamp(row, 0, _terminal.Rows - 1));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Mouse.Capture(this);
        var (col, row) = CellFromPoint(e.GetPosition(this));
        _selAnchor = new Point(col, row);
        _terminal.Selection.StartSelection(col, row, XTerm.Selection.SelectionMode.Normal);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_selAnchor is null || e.LeftButton != MouseButtonState.Pressed) return;
        var (col, row) = CellFromPoint(e.GetPosition(this));
        _terminal.Selection.UpdateSelection(col, row);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_selAnchor is null) return;
        Mouse.Capture(null);
        _selAnchor = null;
        _terminal.Selection.EndSelection();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        e.Handled = true;
        var (col, row) = CellFromPoint(e.GetPosition(this));

        // 应用开启鼠标跟踪（vim/htop 等）：滚轮作为鼠标事件转发
        if (_terminal.MouseTrackingMode != XTerm.Input.MouseTrackingMode.None)
        {
            var ev = e.Delta > 0 ? XMouseEventType.WheelUp : XMouseEventType.WheelDown;
            Emit(_terminal.GenerateMouseEvent(
                e.Delta > 0 ? XMouseButton.WheelUp : XMouseButton.WheelDown,
                col, row, ev, ToMods(Keyboard.Modifiers)));
            return;
        }
        // 备屏无滚回（vim 全屏模式且未开鼠标跟踪）：不滚动
        if (_terminal.IsAlternateBufferActive) return;
        ScrollViewportLines(e.Delta > 0 ? -ScrollWheelLines : ScrollWheelLines);
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
