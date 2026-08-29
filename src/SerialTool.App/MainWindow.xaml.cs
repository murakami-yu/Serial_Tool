using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SerialTool.App.ViewModels;
namespace SerialTool.App;

public partial class MainWindow : Window
{
    private const int RxTrimThreshold = 800_000; // 接收框字符数上限（超出截掉前半，防止无限增长）
    private const int RxTrimKeep = 400_000;

    // 分隔条宽 8、左栏最小 420；右栏余量 2 给 GroupBox 边框留安全距离
    private const double SplitterWidth = 8;
    private const double LeftColumnMinWidth = 420;
    private const double FramesColumnSafety = 2;
    // 下限 = 整表最小需求：固定列 246 + 内容/备注最小宽 200 + 面板内边距 24 + 余量。
    // 再低 DataGrid 会按比例压缩所有列（含固定列），列头会被切字
    private const double FramesPanelMinWidth = 500;

    private double _framesPanelWidth = 500; // 隐藏前记住用户拖出的宽度

    public MainWindow()
    {
        InitializeComponent();
        if (DataContext is MainViewModel vm)
            vm.RxRendered += OnRxRendered;
        Closed += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.RxRendered -= OnRxRendered;
                vm.Dispose();
            }
        };
        // 按持久化设置应用面板初始状态（绑定触发的事件可能早于元素就绪）
        Dispatcher.BeginInvoke(ApplyFramesPanelState, System.Windows.Threading.DispatcherPriority.Loaded);

        // 拖动分隔条过程中持续钳制右栏宽度：本机 DPI 环境异常时（150% 缩放 + 虚拟显示驱动），
        // 拖动增量会被放大数百倍，GridSplitter 会写出远超窗口的列宽，必须当场掐掉
        FramesSplitter.DragDelta += (_, _) => ClampRightColumn();
        FramesSplitter.DragCompleted += (_, _) =>
        {
            ClampRightColumn();
            if (FramesPanel.ActualWidth > 0)
                _framesPanelWidth = FramesPanel.ActualWidth;
        };
    }

    // ---------- 右栏宽度钳制 ----------

    /// <summary>右栏允许的最大宽度。用根 Grid 实际宽度计算（客户区内、已扣边距），
    /// 不能用 Window.ActualWidth——它含窗口边框，会把上限放宽 4~7 DIP 导致右边框被窗口边缘裁切。</summary>
    private double MaxFramesWidth()
        => Math.Max(FramesPanelMinWidth,
           RootGrid.ActualWidth - SplitterWidth - LeftColumnMinWidth - FramesColumnSafety);

    /// <summary>把右栏定义宽度压回窗口可容纳范围。绝对值或 Star 值异常（拖动增量被 DPI 放大）都处理。</summary>
    private void ClampRightColumn()
    {
        var max = MaxFramesWidth();
        if (RightColumnDef is not null && RightColumnDef.Width.Value > max)
            RightColumnDef.Width = new GridLength(max);
    }

    /// <summary>窗口尺寸变化时钳制右栏，防止绝对宽度超出可用空间。</summary>
    private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        => ClampRightColumn();

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
            RightColumnDef.Width = new GridLength(ClampFramesWidth(_framesPanelWidth));
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

    // ---------- 接收框渲染（事件驱动：追加保留滚动位置） ----------

    /// <summary>跟随策略：勾选"跟随最新"且鼠标不在框上（悬停 = 暂停查看）。</summary>
    private bool FollowLatest()
        => DataContext is MainViewModel { AutoScroll: true } && !RxOutput.IsMouseOver;

    private void OnRxRendered(object? sender, RxRender r)
    {
        switch (r.Kind)
        {
            case RxRenderKind.Clear:
                RxOutput.Clear();
                break;

            case RxRenderKind.Append:
                RxOutput.AppendText(r.Text);
                TrimIfNeeded();
                if (FollowLatest())
                    RxOutput.ScrollToEnd();
                break;

            case RxRenderKind.Full:
                // 显示模式切换全量重绘：恢复原滚动位置
                var offset = RxOutput.VerticalOffset;
                RxOutput.Text = r.Text;
                RxOutput.ScrollToVerticalOffset(offset);
                if (FollowLatest())
                    RxOutput.ScrollToEnd();
                break;
        }
    }

    /// <summary>超长截断：丢弃前半部分，保留最新内容。</summary>
    private void TrimIfNeeded()
    {
        if (RxOutput.Text.Length <= RxTrimThreshold) return;
        RxOutput.Text = RxOutput.Text[^RxTrimKeep..];
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
