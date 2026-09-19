using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SerialTool.App.Controls;
using SerialTool.App.Services;
using SerialTool.App.ViewModels;
using SerialTool.Backends;
using SerialTool.Backends.Ssh;
namespace SerialTool.App;

/// <summary>终端独立窗口（多会话标签）：主连接固定第一标签（后端归 MainViewModel），
/// 独立 SSH 会话各自持 backend + TerminalView，关标签即断开。
/// 取消勾选/X=销毁（延迟回写勾选防 Closing 重入）；主窗退出经 CloseForReal 真关并清理全部会话。</summary>
public partial class TerminalWindow : Window
{
    /// <summary>标签页与视图的配对（Session.Tag 存此记录）。</summary>
    private sealed record SessionTab(TerminalSession Session, TabItem Tab, TextBlock HeaderText);

    private readonly MainViewModel _vm;
    private readonly TerminalView _mainView;
    private bool _realClose;

    private readonly SavedSessionsStore _saved;
    private readonly List<SessionTab> _tabs = new();

    public TerminalWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _saved = new SavedSessionsStore(Path.Combine(AppContext.BaseDirectory, "Config", "terminal_sessions.json"));

        // 主连接标签：视图接线沿用 M1/M2（RawRxTap → 入队；输入 → SendTerminalBytes；resize → 通道通知）
        _mainView = new TerminalView();
        var mainSession = TerminalSession.ForMain(_mainView, "主连接（未连接）");
        var mainTab = BuildTab(mainSession, closable: false);
        Sessions.Items.Add(mainTab);

        _mainView.InputEmitted += bytes => _vm.SendTerminalBytes(bytes);
        _mainView.Resized += (cols, rows) => _vm.NotifyTerminalResized(cols, rows);
        _mainView.TitleChanged += () => UpdateWindowTitle();

        _vm.RawRxTap += OnRawRx;
        _vm.PropertyChanged += OnVmPropertyChanged;
        UpdateMainTitle();
        RefreshSavedCombo();

        // 开箱即用：主连接未建立时自动开一个本地终端标签（用户可直接输命令，
        // 不必先连串口/TCP/SSH）；用户主动关掉后不重开（尊重选择）
        if (!_vm.IsPortOpen)
            StartLocalSession();

        Sessions.SelectionChanged += (_, _) => FocusActiveView();
        // 开箱即用配套的焦点修正：构造期自动开本地标签时窗口尚未显示，SelectionChanged 里的
        // Focus() 静默失败——窗口加载完成后补聚焦，否则用户"打开终端窗打字"第一轮按键落空
        Loaded += (_, _) => FocusActiveView();
    }

    /// <summary>读取线程回调：仅入队（线程安全），UI 泵统一消费。</summary>
    private void OnRawRx(object? sender, byte[] bytes) => _mainView.EnqueueBytes(bytes);

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsPortOpen) && _mainOverlay is not null)
            _mainOverlay.Visibility = _vm.IsPortOpen ? Visibility.Collapsed : Visibility.Visible;
        if (e.PropertyName is nameof(MainViewModel.IsPortOpen) or nameof(MainViewModel.ConnTypeIndex)
            or nameof(MainViewModel.TcpHost) or nameof(MainViewModel.SshHost) or nameof(MainViewModel.SelectedDevice))
            UpdateMainTitle();
    }

    private void UpdateMainTitle()
    {
        var tab = _tabs.Find(t => t.Session.IsMain);
        if (tab is null) return;
        tab.HeaderText.Text = _vm.IsPortOpen
            ? _vm.ConnTypeIndex switch
            {
                0 => $"主连接 · {_vm.SelectedDevice?.Id}",
                1 => $"主连接 · TCP {_vm.TcpHost}:{_vm.TcpPort}",
                _ => $"主连接 · SSH {_vm.SshUser}@{_vm.SshHost}:{_vm.SshPort}",
            }
            : "主连接（未连接）";
        UpdateWindowTitle();
    }

    /// <summary>窗口标题：主连接描述 + 终端 OSC 标题（远程 shell 设置时）。</summary>
    private void UpdateWindowTitle()
    {
        var osc = _mainView.Terminal.Title;
        Title = string.IsNullOrEmpty(osc) ? "终端" : $"终端 — {osc}";
    }

    // ---------- 标签构建 ----------

    /// <summary>构建标签页：头 = 状态文本 +（可关标签）×按钮；内容 = 终端画布 + 滚回滚动条。</summary>
    private TabItem BuildTab(TerminalSession session, bool closable)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var headText = new TextBlock { Text = session.Title, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(headText);
        if (closable)
        {
            var close = new Button
            {
                Content = "×",
                Margin = new Thickness(8, 0, 0, 0),
                Padding = new Thickness(4, 0, 4, 0),
                MinWidth = 0,
                MinHeight = 0,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "关闭并断开该会话",
            };
            close.Click += (_, _) => CloseTab(session);
            header.Children.Add(close);
        }

        var view = session.View;
        var bar = new ScrollBar
        {
            Orientation = Orientation.Vertical,
            Width = 14,
            Margin = new Thickness(6, 0, 0, 0),
            Minimum = 0,
            Maximum = 0,
            ViewportSize = 1,
        };
        bar.Scroll += (_, e) => view.ScrollViewport((int)e.NewValue);
        var host = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Controls.TerminalView.ThemeBackground),
            BorderBrush = TryFindResource("BorderBrush") as Brush,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = view,
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(host, 0);
        Grid.SetColumn(bar, 1);
        grid.Children.Add(host);
        grid.Children.Add(bar);

        void Sync()
        {
            if (view.IsAltBuffer)
            {
                bar.Visibility = Visibility.Collapsed;
                return;
            }
            bar.Visibility = Visibility.Visible;
            var max = view.MaxViewportY;
            if (Math.Abs(bar.Maximum - max) > 0.5) bar.Maximum = max;
            bar.ViewportSize = Math.Max(1, view.ViewportRows);
            if (Math.Abs(bar.Value - view.ViewportY) > 0.5) bar.Value = view.ViewportY;
        }
        view.ViewportChanged += Sync;
        view.Resized += (_, _) => Sync();

        if (session.IsMain)
        {
            // 未连接遮罩只属于主连接（独立会话断开保留现场查看）
            var overlay = new Border
            {
                CornerRadius = new CornerRadius(6),
                Background = new SolidColorBrush(Color.FromArgb(230, 0x1E, 0x1E, 0x1E)),
                IsHitTestVisible = false,
                Visibility = _vm.IsPortOpen ? Visibility.Collapsed : Visibility.Visible,
            };
            var tip = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            tip.Children.Add(new TextBlock
            {
                Text = "未连接",
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)),
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            tip.Children.Add(new TextBlock
            {
                Text = "在主窗口打开串口 / TCP / SSH 连接后，对端输出将在此按 VT 终端方式渲染；"
                     + "工具条「+ 本地」可随时开本地 PowerShell 直接输命令（未连接时已自动打开一个）",
                Foreground = new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                FontSize = 12,
                Margin = new Thickness(16, 10, 16, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                MaxWidth = 420,
            });
            overlay.Child = tip;
            Grid.SetColumnSpan(overlay, 2);
            grid.Children.Add(overlay);
            _mainOverlay = overlay;
        }

        var tab = new TabItem { Header = header, Content = grid, Tag = session };
        _tabs.Add(new SessionTab(session, tab, headText));
        return tab;
    }

    private Border? _mainOverlay;

    private void FocusActiveView()
    {
        if (Sessions.SelectedItem is TabItem { Content: Grid g })
            foreach (var child in g.Children)
                if (child is Border { Child: TerminalView v })
                {
                    v.Focus();
                    return;
                }
    }

    // ---------- 独立 SSH 会话 ----------

    private void NewSsh_Click(object sender, RoutedEventArgs e) => OpenSshDialog(prefill: null);

    private void NewTelnet_Click(object sender, RoutedEventArgs e) => OpenTelnetDialog(prefill: null);

    private void NewLocal_Click(object sender, RoutedEventArgs e) => StartLocalSession();

    /// <summary>新建本地终端会话（ConPTY 承载 pwsh → powershell 探测兜底）。</summary>
    private void StartLocalSession()
    {
        var view = new TerminalView();
        Services.ConPtySession con;
        var title = "本地终端";
        try
        {
            con = new Services.ConPtySession(view.Terminal.Cols, view.Terminal.Rows);
            title = con.CommandLine.StartsWith("pwsh") ? "PowerShell"
                : con.CommandLine.StartsWith("powershell") ? "PowerShell 5" : "cmd";
        }
        catch (Exception ex)
        {
            view.Dispose();
            MessageBox.Show(this, $"本地终端启动失败：{ex.Message}", "终端",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var session = TerminalSession.Local(con, view, title);
        var tab = BuildTab(session, closable: true);
        Sessions.Items.Add(tab);
        Sessions.SelectedItem = tab;
    }

    private void ConnectSaved_Click(object sender, RoutedEventArgs e)
    {
        if (SavedCombo.SelectedItem is not SavedSession s)
        {
            MessageBox.Show(this, "请先在右侧下拉选择一个已保存的会话", "终端",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (s.Kind == "telnet")
            OpenTelnetDialog(s);
        else
            OpenSshDialog(s);
    }

    private void DeleteSaved_Click(object sender, RoutedEventArgs e)
    {
        if (SavedCombo.SelectedItem is SavedSession s && _saved.Remove(s.Name))
            RefreshSavedCombo();
    }

    private void RefreshSavedCombo()
    {
        SavedCombo.ItemsSource = _saved.Items;
        if (SavedCombo.SelectedIndex < 0 && _saved.Items.Count > 0)
            SavedCombo.SelectedIndex = 0;
    }

    private void OpenSshDialog(SavedSession? prefill)
    {
        var dlg = new SshConnectDialog(_vm) { Owner = this };
        if (prefill is not null)
            dlg.Prefill(prefill);
        if (dlg.ShowDialog() != true || dlg.Request is not { } req)
            return;

        if (req.SaveToList)
        {
            var name = req.SaveName ?? $"{req.User}@{req.Host}";
            _saved.AddOrUpdate(new SavedSession(name, "ssh", req.Host, req.Port, req.User,
                req.Password is null ? 1 : 0, req.KeyPath ?? ""));
            RefreshSavedCombo();
        }

        var backend = new SshBackend();
        backend.HostKeyVerifying += (_, e) => Dispatcher.Invoke(() => _vm.VerifyHostKey(e));
        var view = new TerminalView();
        var title = $"{req.User}@{req.Host}";
        var session = TerminalSession.Ssh(backend, view, title);

        try
        {
            backend.Open(new SshConfig(req.Host, req.Port, req.User,
                req.Password, req.KeyPath, req.KeyPassphrase, view.Terminal.Cols, view.Terminal.Rows));
        }
        catch (Exception ex)
        {
            session.Dispose();
            MessageBox.Show(this, $"连接失败：{ex.Message}", "新建 SSH 会话",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var tab = BuildTab(session, closable: true);
        Sessions.Items.Add(tab);
        Sessions.SelectedItem = tab;
    }

    private void OpenTelnetDialog(SavedSession? prefill)
    {
        var dlg = new TelnetConnectDialog() { Owner = this };
        if (prefill is not null)
            dlg.Prefill(prefill);
        if (dlg.ShowDialog() != true || dlg.Request is not { } req)
            return;

        if (req.SaveToList)
        {
            var name = req.SaveName ?? $"{req.Host}:{req.Port}";
            _saved.AddOrUpdate(new SavedSession(name, "telnet", req.Host, req.Port, "", 0, ""));
            RefreshSavedCombo();
        }

        var backend = new Backends.Telnet.TelnetBackend();
        var view = new TerminalView();
        var session = TerminalSession.Telnet(backend, view, $"telnet {req.Host}:{req.Port}");
        try
        {
            backend.Open(new Backends.Telnet.TelnetConfig(req.Host, req.Port));
        }
        catch (Exception ex)
        {
            session.Dispose();
            MessageBox.Show(this, $"连接失败：{ex.Message}", "新建 Telnet 会话",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var tab = BuildTab(session, closable: true);
        Sessions.Items.Add(tab);
        Sessions.SelectedItem = tab;
    }

    private void CloseTab(TerminalSession session)
    {
        var tab = _tabs.Find(t => ReferenceEquals(t.Session, session));
        if (tab is null || session.IsMain) return;
        _tabs.Remove(tab);
        Sessions.Items.Remove(tab.Tab);
        session.Dispose();
        Sessions.SelectedIndex = 0;
    }

    // ---------- 窗口生命周期 ----------

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
            foreach (var t in _tabs)
                t.Session.Dispose();
            _tabs.Clear();
        }
        base.OnClosing(e);
    }
}
