using System.Windows;
using SerialTool.App.ViewModels;
namespace SerialTool.App;

/// <summary>图表独立窗口：时序图 / 字段曲线。主窗顶部「波形」复选框控制（状态记忆）。
/// 取消勾选即销毁本窗、重新勾选新建（主窗记忆位置尺寸）——不用 Hide/重显。
/// 用户点 X ⇔ 取消勾选「波形」（延迟回写避免 Closing 重入）；主窗退出或销毁时经 CloseForReal 真正关闭并退订。</summary>
public partial class ChartWindow : Window
{
    private bool _realClose;
    // 空闲保底重绘：本机环境（虚拟显示驱动/窗口遮挡恢复/移动）会出现 Skia 表面失效且
    // 没有任何 WPF 事件可挂钩的情况，1Hz 补帧兜底坐标轴不消失
    private readonly System.Windows.Threading.DispatcherTimer _keepAlive;

    public ChartWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.WaveRendered += OnWaveRendered;
        vm.FieldPlotsRendered += OnFieldPlotsRendered;
        InitWavePlot();
        InitFieldPlot();

        // 空闲（无收发数据）时 FlushRx 早退、渲染事件不触发；本机 ScottPlot 首帧也不主动重绘——
        // 显示完成、缩放、激活、换页后都补一帧（延迟到 Background 优先级，画面挂载渲染之后），
        // 另有 1Hz 保底补帧。历史上坐标轴消失的真正根因是 Popup 挤占 DockPanel 填充位（见 XAML 注释），
        // 补帧机制仅为空闲首帧保险
        SizeChanged += (_, _) => RefreshAllDeferred();
        Activated += (_, _) => RefreshAllDeferred();
        MainTab.SelectionChanged += (_, _) => RefreshAllDeferred();

        _keepAlive = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _keepAlive.Tick += (_, _) => { if (IsVisible) RefreshAll(); };
        _keepAlive.Start();
    }

    private void RefreshAllDeferred()
        => Dispatcher.BeginInvoke(new Action(RefreshAll), System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>主窗退出时调用：绕过「X = 取消勾选」语义，真正关闭。</summary>
    public void CloseForReal()
    {
        _realClose = true;
        Close();
    }

    /// <summary>句柄创建后、首次渲染前标定最小尺寸：此刻 UpdateLayout 反映真实窗口度量且无可见跳动。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        CalibrateMinSize();
    }

    /// <summary>首帧补绘：窗口完成首次渲染后 ScottPlot 才能正常画出来（直接在 Show 后 Refresh 无效，
    /// 本机实测坐标轴会消失直到下一次数据/交互触发重绘）。</summary>
    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        RefreshAll();
    }

    /// <summary>按「所有元素完整显示」实测最小窗口尺寸：清掉最小尺寸约束，SizeToContent 让窗口
    /// 分别按两页内容自适应，读到的实际窗口尺寸即含标题栏/边框的完整显示尺寸，取两页最大值
    /// 写回 MinWidth/MinHeight（字号/DPI/系统主题变化时自动随之变化，无需手调魔法数）。</summary>
    private void CalibrateMinSize()
    {
        var w = Width; var h = Height; var left = Left; var top = Top; var tab = MainTab.SelectedIndex;
        MinWidth = 0; MinHeight = 0;
        SizeToContent = SizeToContent.WidthAndHeight;
        var needW = 0.0; var needH = 0.0;
        for (var i = 0; i < MainTab.Items.Count; i++)
        {
            MainTab.SelectedIndex = i;
            UpdateLayout();
            needW = Math.Max(needW, ActualWidth);
            needH = Math.Max(needH, ActualHeight);
        }
        SizeToContent = SizeToContent.Manual;
        MainTab.SelectedIndex = tab;

        var wa = SystemParameters.WorkArea;
        MinWidth = Math.Min(Math.Ceiling(needW) + 1, wa.Width);
        MinHeight = Math.Min(Math.Ceiling(needH) + 1, wa.Height);
        // 默认尺寸若小于实测最小值则一次性顶到最小值，位置同步收回工作区内
        Width = Math.Max(w, MinWidth);
        Height = Math.Max(h, MinHeight);
        Left = Math.Max(wa.Left, Math.Min(left, wa.Right - Width));
        Top = Math.Max(wa.Top, Math.Min(top, wa.Bottom - Height));
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_realClose)
        {
            e.Cancel = true;
            // 回写「波形 = 取消勾选」必须延迟到本次（已取消的）关闭序列完全结束：同步回写会经
            // 主窗 ApplyWavePanelState 在本窗 Closing 进行中再次 Close()（重入），外层关闭序列
            // 继续在已关闭窗口上执行，抛 InvalidOperationException
            //（"在窗口关闭期间，无法将可见性设置为可见，也无法调用 Show…"，VerifyNotClosing）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (DataContext is MainViewModel vm && vm.ShowWavePanel)
                    vm.ShowWavePanel = false;
            }), System.Windows.Threading.DispatcherPriority.Background);
        }
        else if (DataContext is MainViewModel vm)
        {
            _keepAlive.Stop();
            vm.WaveRendered -= OnWaveRendered;
            vm.FieldPlotsRendered -= OnFieldPlotsRendered;
        }
        base.OnClosing(e);
    }

    /// <summary>补一帧：两块画布按当前快照重绘（表面失效的复活由 ReviveSurfaces 负责）。</summary>
    public void RefreshAll()
    {
        OnWaveRendered(this, EventArgs.Empty);
        OnFieldPlotsRendered(this, EventArgs.Empty);
    }

    /// <summary>页签行「？」：弹出字段曲线页使用说明（点窗口内别处由 StaysOpen=False 自动关闭）。
    /// 按钮位于 TabControl 模板名称域内，Popup 的 PlacementTarget 无法用 ElementName 绑定，在此以 sender 指定。</summary>
    private void FieldPlotHelp_Click(object sender, RoutedEventArgs e)
    {
        FieldPlotHelpPopup.PlacementTarget = sender as UIElement;
        FieldPlotHelpPopup.IsOpen = true;
    }

    // ---------- 时序图（逻辑分析仪式逐位方波） ----------

    private ScottPlot.Plottables.Scatter? _waveRx;
    private ScottPlot.Plottables.Scatter? _waveTx;
    private const double RxHigh = 1.6, RxLow = 1.0;   // RX 通道电平带
    private const double TxHigh = 0.2, TxLow = -0.4;  // TX 通道电平带
    private const double FollowWindowSec = 0.1;       // 跟随窗口 100ms（位级观察）

    /// <summary>初始化时序图：轴与固定纵向范围。
    /// 注意：ScottPlot 5.1.59 的 SkiaSharp 文本链路在此环境无法渲染中文（各级字体设置均试过仍豆腐块，
    /// SkiaSharp 本身解析正常），轴标签用英文；中文图例说明由面板顶部的 WPF 提示行承担。</summary>
    private void InitWavePlot()
    {
        // 图表白底与圆角由外层 WPF Border 提供，ScottPlot 自身背景置透明（否则直角白块盖住圆角）
        WavePlot.Plot.FigureBackground.Color = PlotTransparent;
        WavePlot.Plot.DataBackground.Color = PlotTransparent;
        WavePlot.Plot.Axes.Bottom.Label.Text = "Time (s)";
        WavePlot.Plot.Axes.Left.Label.Text = "RX / TX";
        WavePlot.Plot.Axes.SetLimitsY(-1.0, 2.4);
        WavePlot.Plot.Axes.Left.SetTicks(new[] { (RxHigh + RxLow) / 2, (TxHigh + TxLow) / 2 },
            new[] { "RX", "TX" });
    }

    /// <summary>把跳变序列展开成方波拐点序列（每个跳变 = 保持点 + 边沿点），普通折线即方波。</summary>
    private static void ExpandSquare(double[] xs, double[] ys, double prevHigh, double high, double low,
        out double[] outXs, out double[] outYs)
    {
        if (xs.Length == 0)
        {
            outXs = Array.Empty<double>();
            outYs = Array.Empty<double>();
            return;
        }
        var ox = new List<double>(xs.Length * 2 + 1);
        var oy = new List<double>(ox.Capacity);
        var prevLevel = prevHigh > 0.5 ? high : low;
        ox.Add(xs[0]); oy.Add(prevLevel); // 起始电平
        for (var i = 0; i < xs.Length; i++)
        {
            var lvl = ys[i] > 0.5 ? high : low;
            if (lvl == prevLevel) continue;
            ox.Add(xs[i]); oy.Add(prevLevel); // 保持到跳变时刻
            ox.Add(xs[i]); oy.Add(lvl);       // 垂直边沿
            prevLevel = lvl;
        }
        outXs = ox.ToArray();
        outYs = oy.ToArray();
    }

    /// <summary>重建一条方波曲线（逻辑分析仪电平带）。</summary>
    private ScottPlot.Plottables.Scatter AddWaveStep(double[] xs, double[] ys, double prev, double high, double low, string hex)
    {
        ExpandSquare(xs, ys, prev, high, low, out var ox, out var oy);
        var s = WavePlot.Plot.Add.Scatter(ox, oy);
        s.LineWidth = 1.2f;
        s.MarkerSize = 0;
        s.Color = ScottPlot.Color.FromHtml(hex);
        return s;
    }

    /// <summary>波形刷新：重建 RX/TX 方波；跟随模式锁定最近 100ms（位级观察窗口）。</summary>
    private void OnWaveRendered(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!IsVisible || WavePlot is null) return; // 窗口隐藏时跳过渲染（缓冲继续累计）

        var rx = vm.RxWaveSnapshot();
        var tx = vm.TxWaveSnapshot();

        if (_waveRx is not null) WavePlot.Plot.Remove(_waveRx);
        if (_waveTx is not null) WavePlot.Plot.Remove(_waveTx);
        _waveRx = AddWaveStep(rx.Xs, rx.Ys, rx.Prev, RxHigh, RxLow, "#2B7DE0");
        _waveTx = AddWaveStep(tx.Xs, tx.Ys, tx.Prev, TxHigh, TxLow, "#E08A2B");

        if (vm.WaveFollow)
        {
            var last = 0.0;
            if (rx.Xs.Length > 0) last = Math.Max(last, rx.Xs[^1]);
            if (tx.Xs.Length > 0) last = Math.Max(last, tx.Xs[^1]);
            if (last > 0)
            {
                WavePlot.Plot.Axes.SetLimitsX(last - FollowWindowSec, last + FollowWindowSec * 0.02);
            }
        }
        WavePlot.Refresh();
    }

    // ---------- 字段曲线（帧数据域取值随时间折线） ----------

    private readonly List<ScottPlot.Plottables.Scatter> _fieldScatters = new();
    private static readonly string[] FieldPalette =
        { "#2B7DE0", "#E08A2B", "#3DA35D", "#C1436D", "#7B5CD6", "#00A6A6" };
    private const double FieldFollowWindowSec = 60; // 跟随窗口（语义级曲线看长趋势）

    // 全透明色（5.1.59 无 Color.Transparent 预定义）：图表自身背景让位于外层 WPF 圆角卡片
    private static readonly ScottPlot.Color PlotTransparent = ScottPlot.Color.FromARGB(0);

    private void InitFieldPlot()
    {
        // 同时序图：背景透明，白底圆角由外层 Border 提供；图例去白底方块
        FieldPlotCtl.Plot.FigureBackground.Color = PlotTransparent;
        FieldPlotCtl.Plot.DataBackground.Color = PlotTransparent;
        FieldPlotCtl.Plot.Legend.BackgroundColor = PlotTransparent;
        FieldPlotCtl.Plot.Legend.ShadowColor = PlotTransparent;
        FieldPlotCtl.Plot.Axes.Bottom.Label.Text = "Time (s)";
    }

    /// <summary>曲线刷新：重建各条 Scatter；跟随模式锁定最近 60 秒。
    /// 图例用曲线名（ScottPlot 中文渲染为豆腐块，建议英文命名，单位进顶部提示行说明）。</summary>
    private void OnFieldPlotsRendered(object? sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (!IsVisible || FieldPlotCtl is null) return; // 窗口隐藏时跳过渲染（采样继续累计）

        var snap = vm.FieldPlotSnapshot();

        foreach (var s in _fieldScatters)
            FieldPlotCtl.Plot.Remove(s);
        _fieldScatters.Clear();

        var last = double.NaN;
        for (var i = 0; i < snap.Length; i++)
        {
            var c = snap[i];
            if (c.Xs.Length == 0) continue;
            var s = FieldPlotCtl.Plot.Add.Scatter(c.Xs, c.Ys);
            s.LineWidth = 1.4f;
            s.MarkerSize = 0;
            s.Color = ScottPlot.Color.FromHtml(FieldPalette[i % FieldPalette.Length]);
            s.LegendText = c.Unit.Length == 0 ? c.Name : $"{c.Name} ({c.Unit})";
            _fieldScatters.Add(s);
            if (double.IsNaN(last) || c.Xs[^1] > last)
                last = c.Xs[^1];
        }

        if (_fieldScatters.Count > 0)
            FieldPlotCtl.Plot.ShowLegend();
        if (vm.WaveFollow && !double.IsNaN(last))
            FieldPlotCtl.Plot.Axes.SetLimitsX(last - FieldFollowWindowSec, last + 1);
        FieldPlotCtl.Refresh();
    }
}
