using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SerialTool.App.ViewModels;

namespace SerialTool.App;

public partial class MainWindow : Window
{
    private const int RxTrimThreshold = 800_000; // 接收框字符数上限（超出截掉前半，防止无限增长）
    private const int RxTrimKeep = 400_000;

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
    }

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
            RightColumnDef.Width = new GridLength(_framesPanelWidth);
        }
        else
        {
            // 记住用户拖出的宽度，下次恢复
            if (RightColumnDef.Width.IsAbsolute && RightColumnDef.Width.Value > 0)
                _framesPanelWidth = RightColumnDef.Width.Value;
            RightColumnDef.Width = new GridLength(0);
        }
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
