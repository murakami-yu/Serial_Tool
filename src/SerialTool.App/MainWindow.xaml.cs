using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using SerialTool.App.ViewModels;
namespace SerialTool.App;

public partial class MainWindow : Window
{
    private const int RxTrimThreshold = 800_000; // 接收框字符数上限（超出截掉前半，防止无限增长）
    private const int RxTrimKeep = 400_000;

    // 分隔条带宽 12（= 发送区/串口配置框间隔，2026-09-12 统一）、左栏最小 420；右栏余量 2 给 GroupBox 边框留安全距离
    private const double SplitterWidth = 12;
    private const double LeftColumnMinWidth = 420;
    private const double FramesColumnSafety = 2;
    // 下限 = 整表最小需求：固定列 246 + 内容/备注最小宽 200 + 面板内边距 24 + 余量。
    // 再低 DataGrid 会按比例压缩所有列（含固定列），列头会被切字
    private const double FramesPanelMinWidth = 500;

    private double _framesPanelWidth = 500; // 隐藏前记住用户拖出的宽度

    public MainWindow()
    {
        InitializeComponent();
        // 清掉 RichTextBox 初始空 Paragraph，首行前不留空行
        RxOutput.Document.Blocks.Clear();
        if (DataContext is MainViewModel vm)
        {
            vm.RxRendered += OnRxRendered;
        }
        // 主窗 Closing 先于 owned 窗口的关闭流程：先把图表窗切到真实关闭模式，
        // 否则它的「X = 取消勾选」语义会取消关闭，导致主窗关了进程却不退
        Closing += (_, _) => _chartWindow?.CloseForReal();
        Closed += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.RxRendered -= OnRxRendered;
                vm.Dispose();
            }
        };
        // 按持久化设置应用面板初始状态（绑定触发的事件可能早于元素就绪；
        // 且记忆为 false 时复选框无变化事件，必须在此兜底）
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ApplyFramesPanelState();
            ApplyWavePanelState();
        }), System.Windows.Threading.DispatcherPriority.Loaded);

        // 预览带式拖动：拖动中两面板列宽保持起始值完全静止（本机虚拟显示驱动对「连续失效的重内容
        // 子树」带 ±1~2px 呈现偏移 = 跟手毛刺抖动；静止即零失效零抖动），仅预览层（竖线+半透明带+
        // 宽度标签）跟手；松手一次应用落点宽度。分隔条每条鼠标消息仍会写它自己推算的列宽（本机 DPI
        // 异常下增量放大数百倍、远超窗口），同消息内原样写回起始值覆盖之（渲染只见到最终值=不变）。
        FramesSplitter.DragStarted += (_, _) =>
        {
            _dragStartMouseX = Mouse.GetPosition(this).X;
            _dragStartLeftAct = LeftColumnDef.ActualWidth;
            _dragStartLeftLen = LeftColumnDef.Width;
            _dragStartRightLen = RightColumnDef.Width;
            DragPreview.Visibility = Visibility.Visible;
            UpdateDragPreview();
        };
        FramesSplitter.DragDelta += (_, _) =>
        {
            LeftColumnDef.Width = _dragStartLeftLen;
            RightColumnDef.Width = _dragStartRightLen;
            UpdateDragPreview();
        };
        FramesSplitter.DragCompleted += (_, e) =>
        {
            DragPreview.Visibility = Visibility.Collapsed;
            if (e.Canceled)
            {
                LeftColumnDef.Width = _dragStartLeftLen;
                RightColumnDef.Width = _dragStartRightLen;
                return;
            }
            ApplyDragWidths(); // 松手一次应用：落点即所见
            _framesPanelWidth = RightColumnDef.Width.Value;
        };
    }

    /// <summary>预览层跟手：竖线在鼠标处（收进两列合法区间），半透明带覆盖「落点后右面板将占的区域」，
    /// 标签显示落点右栏宽度。</summary>
    private void UpdateDragPreview()
    {
        var rootW = RootGrid.ActualWidth;
        var maxLeft = Math.Max(LeftColumnMinWidth, rootW - SplitterWidth - FramesPanelMinWidth);
        var x = Math.Clamp(Mouse.GetPosition(RootGrid).X, LeftColumnMinWidth, maxLeft);
        PreviewLine.Margin = new Thickness(x - 1, 0, 0, 0);
        PreviewBand.Margin = new Thickness(x + SplitterWidth - 1, 0, 0, 0);
        PreviewLabel.Margin = new Thickness(Math.Min(x + 12, rootW - 76), 0, 0, 0);
        PreviewLabelText.Text = $"{rootW - SplitterWidth - x:F0} px";
    }

    /// <summary>按拖动起点 + 鼠标 DIP 位移推算两列宽度并写入（松手时调用；不信任 GridSplitter 自身增量）。
    /// 宽度在设备像素空间取整（150% 下小数 DIP 落半设备像素、边缘发虚）；余数归左列，
    /// 保证 左+分隔+右 恒等于根宽（右面板右缘始终贴齐窗口）。</summary>
    private void ApplyDragWidths()
    {
        var s = DeviceScale();
        var rootDev = Math.Round(RootGrid.ActualWidth * s);
        var splitDev = Math.Round(SplitterWidth * s);
        var minLeftDev = Math.Round(LeftColumnMinWidth * s);
        var minRightDev = Math.Round(FramesPanelMinWidth * s);
        var maxRightDev = Math.Round(MaxFramesWidth() * s);
        var delta = Mouse.GetPosition(this).X - _dragStartMouseX;
        var leftDev = Math.Round((_dragStartLeftAct + delta) * s);
        leftDev = Math.Clamp(leftDev, rootDev - splitDev - maxRightDev, rootDev - splitDev - minRightDev);
        leftDev = Math.Max(leftDev, minLeftDev);
        var rightDev = rootDev - splitDev - leftDev;
        LeftColumnDef.Width = new GridLength(leftDev / s);
        RightColumnDef.Width = new GridLength(rightDev / s);
    }

    /// <summary>当前 DPI 缩放（150% 环境 = 1.5）。宽度取整用它把 DIP 折到设备像素空间。</summary>
    private double DeviceScale()
    {
        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11;
        return m is > 0 ? m.Value : 1;
    }

    /// <summary>把列宽收整到设备像素（恢复记忆宽度 / 窗口缩放吸收差值时用）。</summary>
    private double SnapWidth(double dip)
    {
        var s = DeviceScale();
        return Math.Round(dip * s) / s;
    }

    private double _dragStartMouseX;
    private double _dragStartLeftAct;
    private GridLength _dragStartLeftLen;
    private GridLength _dragStartRightLen;

    // ---------- 右栏宽度钳制 ----------

    /// <summary>右栏允许的最大宽度。用根 Grid 实际宽度计算（客户区内、已扣边距），
    /// 不能用 Window.ActualWidth——它含窗口边框，会把上限放宽 4~7 DIP 导致右边框被窗口边缘裁切。</summary>
    private double MaxFramesWidth()
        => Math.Max(FramesPanelMinWidth,
           RootGrid.ActualWidth - SplitterWidth - LeftColumnMinWidth - FramesColumnSafety);

    /// <summary>把右栏定义宽度压回窗口可容纳范围。绝对值或 Star 值异常（拖动增量被 DPI 放大）都处理。</summary>
    /// <summary>窗口尺寸变化时钳制右栏并把差值吸收进左列（右栏保持用户拖出的宽度）。
    /// 根宽用 GetClientRect 现取（设备像素/DPI），不读 RootGrid.ActualWidth：拖动后两列为绝对值，
    /// 其和会垫高 Grid 最小宽，窗口缩小时 arrange 被 MinWidth 顶回、RootGrid.ActualWidth 停在溢出值
    /// （右列伸出窗口右缘被切的老问题根因，HEAD 同机制）；客户区矩形不受该溢出影响。
    /// 事件内一次写完、无中间态（本机 200~1700Hz 消息风暴下任何「先收后放」的中间态都会上屏闪烁）。</summary>
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var s = DeviceScale();
        var rootT = RootTargetWidth();
        var max = SnapWidth(Math.Max(FramesPanelMinWidth, rootT - SplitterWidth - LeftColumnMinWidth - FramesColumnSafety));
        var right = Math.Min(RightColumnDef.Width.Value, max);
        if (LeftColumnDef.Width.IsAbsolute)
        {
            var rightDev = Math.Round(right * s);
            var leftDev = Math.Max(Math.Round(LeftColumnMinWidth * s),
                Math.Round(rootT * s) - Math.Round(SplitterWidth * s) - rightDev);
            LeftColumnDef.Width = new GridLength(leftDev / s);
            right = rightDev / s;
        }
        RightColumnDef.Width = new GridLength(right);
    }

    /// <summary>根 Grid 的目标宽度（DIP）：客户区设备宽 / DPI - 左右 Margin 12×2。</summary>
    private double RootTargetWidth()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetClientRect(hwnd, out var r))
            return r.Right / DeviceScale() - 24;
        return RootGrid.ActualWidth;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CLIENTRECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out CLIENTRECT rect);


    /// <summary>把宽度值收拢到窗口可容纳范围（显示面板/记忆宽度时使用）。</summary>
    private double ClampFramesWidth(double width)
        => Math.Max(FramesPanelMinWidth, Math.Min(width, MaxFramesWidth()));

    // ---------- 多帧面板显示/隐藏 ----------

    private void FramesPanelToggle_Changed(object sender, RoutedEventArgs e)
    {
        // XAML 初始化早期（右栏元素未就绪）由 Loaded 时的初始应用兜底
        if (RightColumnDef is null || FramesPanel is null || FramesSplitter is null)
            return;
        ApplyFramesPanelState();
    }

    private void ApplyFramesPanelState()
    {
        var show = DataContext is MainViewModel { ShowFramesPanel: true };
        if (show)
        {
            RightColumnDef.MinWidth = FramesPanelMinWidth;
            RightColumnDef.Width = new GridLength(SnapWidth(ClampFramesWidth(_framesPanelWidth)));
        }
        else
        {
            // 记住用户拖出的宽度，下次恢复（Star/绝对值都取有效值）
            if (RightColumnDef.Width.Value > 0)
                _framesPanelWidth = RightColumnDef.Width.Value;
            // 列定义带 MinWidth=500，不清零则空列仍占 500px，接收区无法占满全宽
            RightColumnDef.MinWidth = 0;
            RightColumnDef.Width = new GridLength(0);
        }
        // 分隔条拖动会把左列从 * 改成绝对宽度，隐藏后左列不会自动回收空档；
        // 每次切换都重置回 *，并收掉分隔条列，接收区即可占满全宽
        LeftColumnDef.Width = new GridLength(1, GridUnitType.Star);
        SplitterColumnDef.Width = new GridLength(show ? SplitterWidth : 0);
        FramesPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FramesSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 图表窗口（时序 / 字段曲线，独立窗口）显示/隐藏 ----------

    // 每次勾选都新建窗口、取消勾选即销毁（不 Hide）：本机 SkiaSharp 表面在窗口 Hide 后会永久失效
    //（重显后 Refresh/交互全都不再出画面，坐标轴消失的真正根因），重建是唯一可靠恢复方式。
    // 用户拖出的位置/尺寸记在字段里，重开时还原。用户点图表窗的 X ⇔ 取消勾选（走销毁分支）。
    private ChartWindow? _chartWindow;
    private Rect? _chartBounds; // 上次关闭时的窗口位置尺寸（工作区坐标 DIP）

    private void WavePanelToggle_Changed(object sender, RoutedEventArgs e)
    {
        // XAML 初始化期（绑定套用记忆值触发 Checked）：主窗尚未 Show，此时给图表窗设 Owner 会抛
        // 「无法在 Owner 设置为之前未显示的 Window」——由 Loaded 时的初始应用兜底（与多帧面板同一套路）
        if (!IsLoaded) return;
        ApplyWavePanelState();
    }

    private void ApplyWavePanelState()
    {
        if (DataContext is not MainViewModel vm) return;
        if (vm.ShowWavePanel)
        {
            if (_chartWindow is null)
            {
                _chartWindow = new ChartWindow(vm) { Owner = this };
                if (_chartBounds is Rect b)
                    RestoreChartBounds(b);
                else
                    PositionChartWindow(); // 首次打开：贴主窗右侧
            }
            _chartWindow.Show();
        }
        else if (_chartWindow is not null)
        {
            _chartBounds = new Rect(_chartWindow.Left, _chartWindow.Top, _chartWindow.Width, _chartWindow.Height);
            _chartWindow.CloseForReal();
            _chartWindow = null;
        }
    }

    /// <summary>按记忆的位置尺寸重开图表窗，收回工作区内（显示器布局可能已变化）。</summary>
    private void RestoreChartBounds(Rect b)
    {
        if (_chartWindow is null) return;
        var wa = SystemParameters.WorkArea;
        var w = Math.Max(_chartWindow.MinWidth, Math.Min(b.Width, wa.Width));
        var h = Math.Max(_chartWindow.MinHeight, Math.Min(b.Height, wa.Height));
        _chartWindow.Width = w;
        _chartWindow.Height = h;
        _chartWindow.Left = Math.Max(wa.Left, Math.Min(b.X, wa.Right - w));
        _chartWindow.Top = Math.Max(wa.Top, Math.Min(b.Y, wa.Bottom - h));
    }

    /// <summary>图表窗初始位置：贴主窗右侧、顶边对齐；右侧放不下时收回工作区内。</summary>
    private void PositionChartWindow()
    {
        if (_chartWindow is null) return;
        var wa = SystemParameters.WorkArea;
        _chartWindow.Left = Math.Max(wa.Left,
            Math.Min(Left + ActualWidth + 8, wa.Right - _chartWindow.Width));
        _chartWindow.Top = Math.Max(wa.Top,
            Math.Min(Top, wa.Bottom - _chartWindow.Height));
    }

    // ---------- 接收框渲染（事件驱动：追加保留滚动位置；按方向逐行着色） ----------

    // 已渲染字符计数（截断判据；自维护，避免每拍读 RxOutput.Text 全串）
    private int _rxChars;

    /// <summary>跟随策略：勾选"跟随最新"且鼠标不在框上（悬停 = 暂停查看）。</summary>
    private bool FollowLatest()
        => DataContext is MainViewModel { AutoScroll: true } && !RxOutput.IsMouseOver;

    private void OnRxRendered(object? sender, RxRender r)
    {
        switch (r.Kind)
        {
            case RxRenderKind.Clear:
                RxOutput.Document.Blocks.Clear();
                _rxChars = 0;
                break;

            case RxRenderKind.Append:
                AppendSegments(r.Segments);
                TrimIfNeeded();
                if (FollowLatest())
                    RxOutput.ScrollToEnd();
                break;

            case RxRenderKind.Full:
                // 显示模式/颜色切换全量重绘：恢复原滚动位置
                var offset = RxOutput.VerticalOffset;
                RxOutput.Document.Blocks.Clear();
                _rxChars = 0;
                AppendSegments(r.Segments);
                RxOutput.ScrollToVerticalOffset(offset);
                if (FollowLatest())
                    RxOutput.ScrollToEnd();
                break;
        }
    }

    /// <summary>按方向分段追加并着色：一行一个 Paragraph（整行含时间戳/箭头同色），
    /// 直接建 Paragraph/Run 对象树（不走 TextRange，避开空区间着色无效等坑）。</summary>
    private void AppendSegments(IReadOnlyList<RxSeg> segs)
    {
        if (DataContext is not MainViewModel vm) return;
        var doc = RxOutput.Document;
        foreach (var seg in segs)
        {
            var brush = seg.IsTx ? vm.TxBrush : vm.RxBrush;
            var text = seg.Text;
            if (text.Length == 0) continue;
            var start = 0;
            for (var i = 0; i <= text.Length; i++)
            {
                if (i != text.Length && text[i] != '\n') continue;
                if (i > start)
                    doc.Blocks.Add(new Paragraph(new Run(text[start..i])) { Foreground = brush });
                start = i + 1;
            }
            _rxChars += text.Length;
        }
    }

    /// <summary>超长截断：整段（Paragraph = 行）从头部移除，保留最新内容与着色。
    /// 长度用 TextRange 取段落文本实测（TextPointer 偏移计的是符号数，≠ 字符数，禁用）。</summary>
    private void TrimIfNeeded()
    {
        if (_rxChars <= RxTrimThreshold) return;
        var blocks = RxOutput.Document.Blocks;
        var removed = 0;
        while (_rxChars - removed > RxTrimKeep && blocks.Count > 1 && blocks.FirstBlock is Paragraph p)
        {
            removed += new TextRange(p.ContentStart, p.ContentEnd).Text.Length;
            blocks.Remove(p);
        }
        _rxChars -= removed;
    }

    /// <summary>悬停暂停结束：跟随模式下立即补齐到最新。</summary>
    private void RxOutput_MouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is MainViewModel { AutoScroll: true })
            RxOutput.ScrollToEnd();
    }

    /// <summary>重新勾选"跟随最新"：立即跳到最新（若正在悬停则等移出后再跟）。
    /// XAML 初始化期（RxOutput 尚未构造）直接跳过。</summary>
    private void AutoScroll_OnChecked(object sender, RoutedEventArgs e)
    {
        if (RxOutput is null || RxOutput.IsMouseOver)
            return;
        RxOutput.ScrollToEnd();
    }

    // ---------- 发送区 ----------

    /// <summary>Enter 发送（Shift+Enter 换行）。</summary>
    private void TxInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            if (DataContext is MainViewModel vm && vm.SendCommand.CanExecute(null))
                vm.SendCommand.Execute(null);
        }
    }
}
