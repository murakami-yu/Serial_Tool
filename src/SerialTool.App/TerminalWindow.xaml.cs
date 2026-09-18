using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Controls;
using SerialTool.App.Controls;
using SerialTool.App.ViewModels;
namespace SerialTool.App;

/// <summary>终端独立窗口（VT100/xterm 仿真）：主窗顶部「终端」复选框控制（状态记忆）。
/// 取消勾选即销毁、重新勾选新建（主窗记忆位置尺寸）。用户点 X ⇔ 取消勾选（延迟回写避免 Closing 重入）；
/// 主窗退出时经 CloseForReal 真正关闭并退订。RX 原始字节经 RawRxTap（读取线程）→ 视图线程安全入队。</summary>
public partial class TerminalWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly TerminalView _view;
    private bool _realClose;

    public TerminalWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _view = new TerminalView();
        TerminalHost.Child = _view;

        _view.InputEmitted += bytes => _vm.SendTerminalBytes(bytes);
        _view.ViewportChanged += SyncScrollbar;
        _view.Resized += (_, _) => SyncScrollbar();
        _view.TitleChanged += () =>
            Title = string.IsNullOrEmpty(_view.Terminal.Title) ? "终端" : $"终端 — {_view.Terminal.Title}";

        _vm.RawRxTap += OnRawRx;
        _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateOverlay();
    }

    /// <summary>读取线程回调：仅入队（线程安全），UI 泵统一消费。</summary>
    private void OnRawRx(object? sender, byte[] bytes) => _view.EnqueueBytes(bytes);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPortOpen))
            UpdateOverlay();
    }

    private void UpdateOverlay()
        => DisconnectedOverlay.Visibility = _vm.IsPortOpen ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>滚动条同步：值 = 视口顶部行；Maximum = 滚回总行数 - 视口行数。
    /// 编程赋值 Value 不触发 Scroll 事件（WPF ScrollBar.Scroll 仅用户交互），无回环。</summary>
    private void SyncScrollbar()
    {
        if (_view.IsAltBuffer)
        {
            TermScroll.Visibility = Visibility.Collapsed;
            return;
        }
        TermScroll.Visibility = Visibility.Visible;
        var max = _view.MaxViewportY;
        if (Math.Abs(TermScroll.Maximum - max) > 0.5) TermScroll.Maximum = max;
        TermScroll.ViewportSize = Math.Max(1, _view.ViewportRows);
        if (Math.Abs(TermScroll.Value - _view.ViewportY) > 0.5) TermScroll.Value = _view.ViewportY;
    }

    private void TermScroll_Scroll(object sender, ScrollEventArgs e)
        => _view.ScrollViewport((int)e.NewValue);

    /// <summary>主窗退出时调用：绕过「X = 取消勾选」语义，真正关闭。</summary>
    public void CloseForReal()
    {
        _realClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_realClose)
        {
            e.Cancel = true;
            // 回写「终端 = 取消勾选」延迟到关闭序列结束：同步回写会经主窗 ApplyTerminalPanelState
            // 在本窗 Closing 进行中再次 Close()（重入抛 InvalidOperationException，同图表窗）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_vm.ShowTerminalPanel)
                    _vm.ShowTerminalPanel = false;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        else
        {
            _vm.RawRxTap -= OnRawRx;
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _view.Dispose();
        }
        base.OnClosing(e);
    }
}
