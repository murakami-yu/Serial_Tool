using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialTool.App.Services;
using SerialTool.Core;
using SerialTool.Core.Framing;
using SerialTool.Backends;
using SerialTool.Backends.Serial;
using SerialTool.Backends.Tcp;

namespace SerialTool.App.ViewModels;

/// <summary>接收/发送的一行记录。</summary>
/// <param name="Ts">时间戳。</param>
/// <param name="Bytes">原始字节。</param>
/// <param name="IsTx">true = 本机发送（显示 →）。</param>
/// <param name="Frame">解析出的帧（原始数据行为 null）。</param>
/// <param name="Tag">方向前缀覆盖（如自动应答回显 "⇄ "）；空用默认方向箭头。</param>
public sealed record RxItem(DateTime Ts, byte[] Bytes, bool IsTx, ParsedFrame? Frame = null, string? Tag = null);

/// <summary>接收框渲染指令（事件驱动，视图直接操作文本以保留滚动位置）。</summary>
public enum RxRenderKind
{
    /// <summary>追加新内容（不重置滚动）。</summary>
    Append,

    /// <summary>清空。</summary>
    Clear,

    /// <summary>全量重绘（显示模式切换），视图恢复原滚动位置。</summary>
    Full,
}

public sealed record RxRender(RxRenderKind Kind, string Text);

/// <summary>波形跳变点：时刻 + 新电平（逻辑分析仪式逐位重建）。</summary>
public sealed record WavePt(double T, double Y);

/// <summary>主窗口视图模型：端口管理 + 收发控制台。</summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 2000;      // 行缓冲上限（超出丢弃最旧行）
    private const int FlushIntervalMs = 50; // UI 批量刷新周期
    private const int CyclicTickMs = 50;    // 循环发送调度粒度（也是最小周期）
    private const int MaxWavePoints = 60_000;   // 波形跳变点上限（超出丢最旧一半）
    private const int WaveTrimKeep = 30_000;
    private const double TcpNominalBaud = 115200; // TCP 模式无波特率，按标称值重建位宽
    private readonly DispatcherTimer _cyclicTimer;

    // 逻辑分析仪式波形：RX/TX 双通道电平跳变序列（读取线程写、UI 线程读快照）
    private readonly object _waveLock = new();
    private readonly List<WavePt> _rxWave = new();
    private readonly List<WavePt> _txWave = new();
    private double _rxPrev = 1; // 空闲高电平
    private double _txPrev = 1;
    private DateTime _waveStart = DateTime.Now;

    // 字段曲线：读线程写点 / UI 线程拉快照（与波形同模式）
    private const int MaxPlotPoints = 20_000;  // 单条曲线上限（超出丢最旧一半）
    private const int PlotTrimKeep = 10_000;
    private readonly object _plotLock = new();

    private readonly SerialBackend _serialBackend = new();
    private readonly TcpBackend _tcpBackend = new();

    /// <summary>当前活动连接（串口或 TCP），未连接为 null。</summary>
    private IBusBackend? _active;

    private readonly SessionLogger _logger = new();
    private readonly ConcurrentQueue<RxItem> _rxQueue = new();
    private readonly List<RxItem> _lines = new();
    private readonly DispatcherTimer _flushTimer;
    private long _pendingRxBytes;

    // 状态栏统计：近 5 秒 RX 速率滑动窗口 + 连接起始时刻（UI 线程访问）
    private const int RateWindowSec = 5;
    private readonly Queue<(DateTime T, long Bytes)> _rateWindow = new();
    private DateTime? _connectedSince;

    [ObservableProperty]
    private ObservableCollection<DeviceInfo> _portItems = new();

    [ObservableProperty]
    private DeviceInfo? _selectedDevice;

    /// <summary>连接方式：0 = 串口，1 = TCP。</summary>
    [ObservableProperty]
    private int _connTypeIndex;

    [ObservableProperty]
    private string _tcpHost = "192.168.1.100";

    [ObservableProperty]
    private int _tcpPort = 8899;

    public bool IsSerial => ConnTypeIndex == 0;
    public bool IsTcp => ConnTypeIndex != 0;

    [ObservableProperty]
    private int _selectedBaud = 115200;

    [ObservableProperty]
    private int _selectedDataBits = 8;

    [ObservableProperty]
    private int _selectedStopBitsIndex;

    [ObservableProperty]
    private int _selectedParityIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPortClosed))]
    private bool _isPortOpen;

    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>右侧多帧面板是否显示（持久化到 Config/ui_settings.json）。</summary>
    [ObservableProperty]
    private bool _showFramesPanel = true;

    /// <summary>时序图面板是否显示（持久化）。</summary>
    [ObservableProperty]
    private bool _showWavePanel = true;

    /// <summary>时序图是否跟随最新（持久化；取消后可自由缩放平移）。</summary>
    [ObservableProperty]
    private bool _waveFollow = true;

    // ---------- 帧解析 ----------

    [ObservableProperty]
    private bool _parseEnabled;

    [ObservableProperty]
    private FrameTemplate? _selectedTemplate;

    [ObservableProperty]
    private long _frameOkCount;

    [ObservableProperty]
    private long _frameErrCount;

    /// <summary>协议模板列表（持久化到 Config/frame_templates.json）。</summary>
    public ObservableCollection<FrameTemplate> Templates { get; } = new();

    private MultiFrameParser? _parser;

    [ObservableProperty]
    private string _txInput = string.Empty;

    [ObservableProperty]
    private bool _txHexMode = true;

    [ObservableProperty]
    private bool _showHex = true;

    [ObservableProperty]
    private bool _showTimestamp = true;

    [ObservableProperty]
    private string _statusText = "就绪";

    [ObservableProperty]
    private long _rxCount;

    [ObservableProperty]
    private long _txCount;

    [ObservableProperty]
    private bool _logEnabled;

    [ObservableProperty]
    private string _logFilePath = DefaultLogPath();

    /// <summary>接收区过滤词：HEX 子串（忽略分隔/大小写）或显示文本子串。</summary>
    [ObservableProperty]
    private string _rxFilterText = string.Empty;

    /// <summary>过滤开关：只影响接收区视图，数据缓冲与日志文件始终全量。</summary>
    [ObservableProperty]
    private bool _filterEnabled;

    /// <summary>近 5 秒 RX 实测速率（状态栏文本，如 "4.2 kB/s"）。</summary>
    [ObservableProperty]
    private string _rxRateText = "0 B/s";

    /// <summary>本次连接时长 hh:mm:ss（未连接为空）。</summary>
    [ObservableProperty]
    private string _elapsedText = string.Empty;

    // ---------- 控制引脚（RTS/DTR 输出，CTS/DSR 输入指示；仅串口模式） ----------

    /// <summary>RTS 输出电平（写通到端口；打开端口时同步端口实际初值）。</summary>
    [ObservableProperty]
    private bool _rtsOn;

    /// <summary>DTR 输出电平（写通到端口）。</summary>
    [ObservableProperty]
    private bool _dtrOn;

    /// <summary>CTS 输入电平（50ms 轮询刷新）。</summary>
    [ObservableProperty]
    private bool _ctsOn;

    /// <summary>DSR 输入电平（50ms 轮询刷新）。</summary>
    [ObservableProperty]
    private bool _dsrOn;

    /// <summary>引脚控制是否可用：串口模式且已连接（TCP 无物理引脚）。</summary>
    public bool CanControlPins => IsPortOpen && IsSerial;

    partial void OnRtsOnChanged(bool value) => _serialBackend.RtsEnabled = value;

    partial void OnDtrOnChanged(bool value) => _serialBackend.DtrEnabled = value;

    public bool IsPortClosed => !IsPortOpen;

    /// <summary>接收框渲染事件：视图订阅后直接操作 TextBox（追加保留滚动位置）。</summary>
    public event EventHandler<RxRender>? RxRendered;

    /// <summary>波形刷新事件：视图订阅后拉取快照更新曲线（FlushRx 触发）。</summary>
    public event EventHandler? WaveRendered;

    /// <summary>字段曲线刷新事件：视图订阅后拉取快照（FlushRx / 清空 / 配置变更触发）。</summary>
    public event EventHandler? FieldPlotsRendered;

    public IReadOnlyList<int> BaudRates { get; } = new[]
        { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    public IReadOnlyList<int> DataBitsOptions { get; } = new[] { 8, 7 };
    public IReadOnlyList<string> StopBitsOptions { get; } = new[] { "1", "1.5", "2" };
    public IReadOnlyList<string> ParityOptions { get; } = new[] { "无", "偶", "奇" };

    /// <summary>多帧发送列表（持久化到 Config/send_frames.json）。</summary>
    public ObservableCollection<SendFrameViewModel> SendFrames { get; } = new();

    [ObservableProperty]
    private SendFrameViewModel? _selectedFrame;

    // ---------- 字段曲线 ----------

    /// <summary>曲线配置列表（持久化到 Config/field_plots.json）。读线程枚举点集需持 _plotLock。</summary>
    public ObservableCollection<FieldPlotViewModel> FieldPlots { get; } = new();

    [ObservableProperty]
    private FieldPlotViewModel? _selectedPlot;

    // ---------- 自动应答 ----------

    /// <summary>应答规则列表（持久化到 Config/auto_reply.json）。仅 UI 线程访问。</summary>
    public ObservableCollection<AutoReplyViewModel> AutoReplies { get; } = new();

    [ObservableProperty]
    private AutoReplyViewModel? _selectedReply;

    /// <summary>待发应答队列（匹配时入队，FlushRx 按到期时刻发出；仅 UI 线程访问）。</summary>
    private readonly List<(DateTime Due, byte[] Bytes, string Label)> _pendingReplies = new();

    public MainViewModel()
    {
        _serialBackend.DataReceived += OnDataReceived;
        _serialBackend.ErrorOccurred += OnBackendError;
        _tcpBackend.DataReceived += OnDataReceived;
        _tcpBackend.ErrorOccurred += OnBackendError;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(FlushIntervalMs),
        };
        _flushTimer.Tick += FlushRx;
        _flushTimer.Start();

        // 循环发送调度：单一定时器统一驱动所有循环帧，实际周期由各自 PeriodMs 决定。
        _cyclicTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(CyclicTickMs),
        };
        _cyclicTimer.Tick += CyclicTick;
        _cyclicTimer.Start();

        SendFrames.CollectionChanged += OnSendFramesChanged;
        FieldPlots.CollectionChanged += OnFieldPlotsChanged;

        // 预创建日志目录：保证"打开目录"按钮始终有目录可开
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFilePath)!); }
        catch { /* 目录创建失败时打开按钮会提示 */ }

        LoadUiSettings();
        LoadTemplates();
        _ = LoadPortsAsync();
        LoadFrames();
        LoadPlots();
        LoadReplies();
    }

    // ---------- 帧解析联动 ----------

    partial void OnParseEnabledChanged(bool value) => RebuildParser();

    partial void OnSelectedTemplateChanged(FrameTemplate? value) => RebuildParser();

    /// <summary>按启用模板集合重建多模板解析器（编辑器保存后亦调用）。</summary>
    public void RebuildParser()
    {
        if (_parser != null)
            _parser.FrameEmitted -= OnFrameEmitted;
        _parser = null;
        if (!ParseEnabled) return;

        var active = Templates.Where(t => t.Enabled).ToList();
        if (active.Count == 0)
        {
            StatusText = "没有启用的模板（在模板编辑器中勾选\"启用\"）";
            return;
        }
        try
        {
            _parser = new MultiFrameParser(active);
            _parser.FrameEmitted += OnFrameEmitted;
            StatusText = $"帧解析开启: {active.Count} 个模板并行仲裁";
        }
        catch (Exception ex)
        {
            ParseEnabled = false; // 触发本方法重入，清理解析器
            StatusText = $"解析器构建失败: {ex.Message}";
        }
    }

    /// <summary>解析线程回调：帧入队（帧模式下原始字节流不再逐块显示）。
    /// 成功帧同时喂字段曲线采样（读线程上下文：只做加锁写点，不做任何 UI）。</summary>
    private void OnFrameEmitted(ParsedFrame frame)
    {
        _rxQueue.Enqueue(new RxItem(frame.Ts, frame.Raw, IsTx: false, frame));
        if (!frame.Ok) return;

        lock (_plotLock)
        {
            if (FieldPlots.Count == 0) return;
            var t = (frame.Ts - _waveStart).TotalSeconds;
            foreach (var p in FieldPlots)
            {
                if (!p.Enabled) continue;
                var v = FieldPlotEvaluator.Evaluate(p.Snapshot, frame);
                if (v is not { } y) continue;
                p.Pts.Add(new PlotPt(t, y));
                if (p.Pts.Count > MaxPlotPoints)
                    p.Pts.RemoveRange(0, p.Pts.Count - PlotTrimKeep);
            }
        }
    }

    /// <summary>曲线快照（UI 线程拉取；锁内拷贝数组）。</summary>
    public (string Name, string Unit, double[] Xs, double[] Ys)[] FieldPlotSnapshot()
    {
        lock (_plotLock)
        {
            var list = new (string, string, double[], double[])[FieldPlots.Count];
            for (var i = 0; i < FieldPlots.Count; i++)
            {
                var p = FieldPlots[i];
                var xs = new double[p.Pts.Count];
                var ys = new double[p.Pts.Count];
                for (var j = 0; j < p.Pts.Count; j++)
                {
                    xs[j] = p.Pts[j].T;
                    ys[j] = p.Pts[j].Y;
                }
                list[i] = (p.Name, p.Unit, xs, ys);
            }
            return list;
        }
    }

    /// <summary>打开模板编辑窗口。</summary>
    [RelayCommand]
    private void OpenTemplateEditor()
    {
        var win = new TemplateEditorWindow(this)
        {
            Owner = Application.Current?.MainWindow,
        };
        win.Show();
    }

    // ---------- 模板持久化 ----------

    private static string TemplatesPath
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "frame_templates.json");

    private void LoadTemplates()
    {
        try
        {
            if (File.Exists(TemplatesPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(TemplatesPath));
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        FrameTemplate? t = null;
                        try
                        {
                            t = FrameTemplate.MigrateV1(el) ?? el.Deserialize<FrameTemplate>();
                        }
                        catch
                        {
                            // 单项损坏跳过
                        }
                        if (t is null) continue;
                        try { t.Validate(); } catch { continue; }
                        Templates.Add(t);
                    }
                }
            }
        }
        catch
        {
            // 配置损坏时使用默认模板
        }
        if (Templates.Count == 0)
        {
            foreach (var t in FrameTemplate.Samples())
                Templates.Add(t);
            SaveTemplates();
        }
        SelectedTemplate = Templates[0];
    }

    /// <summary>保存模板列表（编辑窗口调用）。</summary>
    public void SaveTemplates()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(TemplatesPath)!);
            File.WriteAllText(TemplatesPath,
                JsonSerializer.Serialize(Templates.ToList(), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            StatusText = $"模板保存失败: {ex.Message}";
        }
    }

    // ---------- 连接方式联动 ----------

    partial void OnConnTypeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsTcp));
        OnPropertyChanged(nameof(CanControlPins));
        TogglePortCommand.NotifyCanExecuteChanged();
        // 切换连接方式时若已连接则先断开
        if (IsPortOpen)
        {
            _active?.Close();
            _active = null;
            IsPortOpen = false;
            OnLinkClosed();
            StatusText = "已断开（切换连接方式）";
        }
    }

    /// <summary>默认日志路径：exe 目录下 Logs/serial_日期_时间.txt。</summary>
    private static string DefaultLogPath()
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Logs",
            $"serial_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

    // ---------- 命令 ----------

    /// <summary>扫描串口（含 WMI 设备名，后台线程执行避免卡 UI）。</summary>
    [RelayCommand]
    private async Task RefreshPorts()
    {
        var items = await Task.Run(_serialBackend.Scan);
        PortItems = new ObservableCollection<DeviceInfo>(items);
        if (SelectedDevice is null || items.All(d => d.Id != SelectedDevice.Id))
            SelectedDevice = items.FirstOrDefault();
        StatusText = items.Count > 0
            ? $"发现 {items.Count} 个串口"
            : "未发现串口（插入设备后点刷新）";
    }

    private async Task LoadPortsAsync() => await RefreshPorts();

    [RelayCommand(CanExecute = nameof(CanTogglePort))]
    private void TogglePort()
    {
        if (IsPortOpen)
        {
            _active?.Close();
            _active = null;
            IsPortOpen = false;
            OnLinkClosed();
            StatusText = "已断开";
            return;
        }

        try
        {
            if (IsSerial)
            {
                _serialBackend.Open(new SerialPortConfig(
                    SelectedDevice!.Id, SelectedBaud, SelectedDataBits,
                    (SerialParity)SelectedParityIndex,
                    (SerialStopBits)SelectedStopBitsIndex));
                _active = _serialBackend;
                StatusText = $"已打开 {SelectedDevice.Id} @ {SelectedBaud}";
            }
            else
            {
                _tcpBackend.Open(new TcpConfig(TcpHost.Trim(), TcpPort));
                _active = _tcpBackend;
                StatusText = $"TCP {TcpHost.Trim()}:{TcpPort} 已连接";
            }
            IsPortOpen = true;
            _connectedSince = DateTime.Now;
            if (IsSerial)
            {
                // 开关状态与端口实际电平同步（RJCP 打开后的默认电平读回），防 UI 残留
                RtsOn = _serialBackend.RtsEnabled;
                DtrOn = _serialBackend.DtrEnabled;
            }
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败: {ex.Message}";
        }
    }

    /// <summary>连接断开后的统计复位（时长清零、速率窗口清空、引脚状态复位）。</summary>
    private void OnLinkClosed()
    {
        _connectedSince = null;
        _rateWindow.Clear();
        RxRateText = "0 B/s";
        ElapsedText = string.Empty;
        RtsOn = false;
        DtrOn = false;
        CtsOn = false;
        DsrOn = false;
    }

    private bool CanTogglePort()
        => IsPortOpen
           || (IsSerial
               ? SelectedDevice is not null
               : !string.IsNullOrWhiteSpace(TcpHost) && TcpPort is > 0 and <= 65535);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = TxInput;
        if (TxHexMode)
        {
            if (!Hex.TryParse(text, out var bytes))
            {
                StatusText = "HEX 格式错误：需要偶数个合法十六进制字符";
                return;
            }
            WriteBytes(bytes);
        }
        else
        {
            WriteBytes(Encoding.UTF8.GetBytes(text));
        }
    }

    private bool CanSend() => IsPortOpen && !string.IsNullOrWhiteSpace(TxInput);

    /// <summary>写入当前活动连接并回显；silent=true 时不刷状态栏（循环发送/自动应答防噪音）。</summary>
    private void WriteBytes(byte[] bytes, string? label = null, bool silent = false, string? tag = null)
    {
        if (bytes.Length == 0) return;
        if (_active is null)
        {
            StatusText = "连接未打开";
            return;
        }
        try
        {
            _active.Write(bytes);
        }
        catch (Exception ex)
        {
            StatusText = $"发送失败: {ex.Message}";
            return;
        }
        _rxQueue.Enqueue(new RxItem(DateTime.Now, bytes, IsTx: true, Tag: tag));
        AppendWave(_txWave, bytes, DateTime.Now, ref _txPrev);
        TxCount += bytes.Length;
        if (!silent)
            StatusText = label is null ? $"已发送 {bytes.Length} 字节" : $"已发送 {label}（{bytes.Length} 字节）";
    }

    // ---------- 多帧发送 ----------

    /// <summary>发送指定帧（手动点击或循环调度触发）。</summary>
    internal void SendFrame(SendFrameViewModel frame)
    {
        if (!IsPortOpen)
        {
            StatusText = "请先打开串口";
            return;
        }
        if (frame.IsHex)
        {
            if (!Hex.TryParse(frame.Content, out var bytes))
            {
                StatusText = $"帧 #{frame.Index} HEX 格式错误";
                return;
            }
            WriteBytes(bytes, $"帧 #{frame.Index}");
        }
        else
        {
            WriteBytes(Encoding.UTF8.GetBytes(frame.Content), $"帧 #{frame.Index}");
        }
    }

    /// <summary>循环调度：周期到点的帧发送。实际周期不小于调度粒度。</summary>
    private void CyclicTick(object? sender, EventArgs e)
    {
        if (!IsPortOpen) return;
        var now = DateTime.Now;
        foreach (var f in SendFrames)
        {
            if (!f.IsCyclic) continue;
            var period = Math.Max(f.PeriodMs, CyclicTickMs);
            if (now < f.NextDue) continue;
            f.NextDue = now.AddMilliseconds(period);
            if (f.IsHex)
            {
                if (!Hex.TryParse(f.Content, out var bytes) || bytes.Length == 0) continue;
                WriteBytes(bytes, $"帧 #{f.Index}", silent: true);
            }
            else
            {
                var bytes = Encoding.UTF8.GetBytes(f.Content);
                if (bytes.Length > 0) WriteBytes(bytes, $"帧 #{f.Index}", silent: true);
            }
        }
    }

    [RelayCommand]
    private void AddFrame()
    {
        var frame = new SendFrameViewModel(this) { PeriodMs = 1000 };
        frame.PropertyChanged += OnFramePropertyChanged;
        SendFrames.Add(frame);
    }

    [RelayCommand(CanExecute = nameof(CanRemoveFrame))]
    private void RemoveSelectedFrame()
    {
        if (SelectedFrame is null) return;
        SelectedFrame.PropertyChanged -= OnFramePropertyChanged;
        SendFrames.Remove(SelectedFrame);
    }

    private bool CanRemoveFrame() => SelectedFrame is not null;

    [RelayCommand]
    private void ClearFrames()
    {
        foreach (var f in SendFrames)
            f.PropertyChanged -= OnFramePropertyChanged;
        SendFrames.Clear();
    }

    private void OnSendFramesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        for (var i = 0; i < SendFrames.Count; i++)
            SendFrames[i].Index = i + 1;
        SaveFrames();
    }

    private void OnFramePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SendFrameViewModel.Content)
            or nameof(SendFrameViewModel.Note)
            or nameof(SendFrameViewModel.IsHex)
            or nameof(SendFrameViewModel.PeriodMs)
            or nameof(SendFrameViewModel.IsCyclic))
            SaveFrames();
    }

    // ---------- 帧配置持久化 ----------

    private sealed record FrameDto(string Content, string Note, bool IsHex, int PeriodMs, bool IsCyclic);

    private static string FramesConfigPath
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "send_frames.json");

    private void LoadFrames()
    {
        try
        {
            if (File.Exists(FramesConfigPath))
            {
                var dto = JsonSerializer.Deserialize<List<FrameDto>>(File.ReadAllText(FramesConfigPath));
                if (dto is not null)
                    foreach (var d in dto)
                    {
                        var frame = new SendFrameViewModel(this)
                        {
                            Content = d.Content,
                            Note = d.Note ?? string.Empty, // 旧版配置无 Note 字段
                            IsHex = d.IsHex,
                            PeriodMs = d.PeriodMs,
                            IsCyclic = d.IsCyclic,
                        };
                        frame.PropertyChanged += OnFramePropertyChanged;
                        SendFrames.Add(frame);
                    }
            }
        }
        catch
        {
            // 配置损坏时回退到默认空帧
        }
        if (SendFrames.Count == 0)
            for (var i = 0; i < 8; i++)
                AddFrame();
    }

    private void SaveFrames()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FramesConfigPath)!);
            var dto = SendFrames.Select(f => new FrameDto(f.Content, f.Note, f.IsHex, f.PeriodMs, f.IsCyclic)).ToList();
            File.WriteAllText(FramesConfigPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            StatusText = $"帧配置保存失败: {ex.Message}";
        }
    }

    // ---------- 字段曲线配置持久化 ----------

    private sealed record PlotDto(
        bool Enabled, string Name, string Template, string CommandHex,
        int Offset, int Width, bool BigEndian, bool Signed, double Scale, string Unit);

    private static string PlotsConfigPath
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "field_plots.json");

    private void LoadPlots()
    {
        try
        {
            if (File.Exists(PlotsConfigPath))
            {
                var dto = JsonSerializer.Deserialize<List<PlotDto>>(File.ReadAllText(PlotsConfigPath));
                if (dto is not null)
                    foreach (var d in dto)
                        AddPlotCore(MapPlot(d));
            }
        }
        catch
        {
            // 配置损坏时回退到默认样例
        }
        if (FieldPlots.Count == 0)
        {
            AddPlotCore(MapPlot(new PlotDto(true, "value", "", "", 0, 2, false, false, 0.01, "")));
            SavePlots();
        }
    }

    private static FieldPlotViewModel MapPlot(PlotDto d) => new()
    {
        Enabled = d.Enabled,
        Name = d.Name,
        Template = d.Template ?? string.Empty,
        CommandHex = d.CommandHex ?? string.Empty,
        Offset = d.Offset,
        Width = d.Width,
        BigEndian = d.BigEndian,
        Signed = d.Signed,
        Scale = d.Scale,
        Unit = d.Unit ?? string.Empty,
    };

    private void SavePlots()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PlotsConfigPath)!);
            var dto = FieldPlots.Select(p => new PlotDto(
                p.Enabled, p.Name, p.Template, p.CommandHex,
                p.Offset, p.Width, p.BigEndian, p.Signed, p.Scale, p.Unit)).ToList();
            File.WriteAllText(PlotsConfigPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            StatusText = $"曲线配置保存失败: {ex.Message}";
        }
    }

    /// <summary>入集合并挂配置变化回调（集合变更与读线程枚举同锁）。</summary>
    private void AddPlotCore(FieldPlotViewModel plot)
    {
        plot.ConfigChanged += OnPlotConfigChanged;
        lock (_plotLock)
        {
            FieldPlots.Add(plot);
        }
    }

    [RelayCommand]
    private void AddPlot()
    {
        var n = FieldPlots.Count + 1;
        AddPlotCore(new FieldPlotViewModel { Name = $"curve{n}" });
        SelectedPlot = FieldPlots[^1];
        SavePlots();
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlot))]
    private void RemoveSelectedPlot()
    {
        if (SelectedPlot is null) return;
        SelectedPlot.ConfigChanged -= OnPlotConfigChanged;
        lock (_plotLock)
        {
            FieldPlots.Remove(SelectedPlot);
        }
        SavePlots();
        FieldPlotsRendered?.Invoke(this, EventArgs.Empty);
    }

    private bool CanRemovePlot() => SelectedPlot is not null;

    [RelayCommand]
    private void ClearPlots()
    {
        foreach (var p in FieldPlots)
            p.ConfigChanged -= OnPlotConfigChanged;
        lock (_plotLock)
        {
            FieldPlots.Clear();
        }
        SavePlots();
        FieldPlotsRendered?.Invoke(this, EventArgs.Empty);
    }

    private void OnFieldPlotsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => OnPropertyChanged(nameof(CanRemovePlot));

    /// <summary>单条曲线配置变更：清该曲线历史点（新旧语义不混画）+ 自动保存 + 重绘。</summary>
    private void OnPlotConfigChanged(FieldPlotViewModel p)
    {
        lock (_plotLock)
        {
            p.Pts.Clear();
        }
        SavePlots();
        FieldPlotsRendered?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 自动应答持久化 ----------

    private sealed record ReplyDto(
        bool Enabled, string Name, string Template, string CommandHex,
        string MatchHex, string ReplyHex, int DelayMs, bool Once);

    private static string RepliesConfigPath
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "auto_reply.json");

    private void LoadReplies()
    {
        try
        {
            if (File.Exists(RepliesConfigPath))
            {
                var dto = JsonSerializer.Deserialize<List<ReplyDto>>(File.ReadAllText(RepliesConfigPath));
                if (dto is not null)
                    foreach (var d in dto)
                        AddReplyCore(new AutoReplyViewModel
                        {
                            Enabled = d.Enabled,
                            Name = d.Name,
                            Template = d.Template ?? string.Empty,
                            CommandHex = d.CommandHex ?? string.Empty,
                            MatchHex = d.MatchHex ?? string.Empty,
                            ReplyHex = d.ReplyHex ?? string.Empty,
                            DelayMs = d.DelayMs,
                            Once = d.Once,
                        });
            }
        }
        catch
        {
            // 配置损坏时回退到空规则
        }
        if (AutoReplies.Count == 0)
        {
            for (var i = 0; i < 2; i++)
                AddReplyCore(new AutoReplyViewModel { Name = $"reply{i + 1}" });
            // 与多帧面板一致：首次生成种子规则即落盘，保证配置文件存在
            SaveReplies();
        }
    }

    private void SaveReplies()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RepliesConfigPath)!);
            var dto = AutoReplies.Select(r => new ReplyDto(
                r.Enabled, r.Name, r.Template, r.CommandHex,
                r.MatchHex, r.ReplyHex, r.DelayMs, r.Once)).ToList();
            File.WriteAllText(RepliesConfigPath,
                JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            StatusText = $"应答配置保存失败: {ex.Message}";
        }
    }

    private void AddReplyCore(AutoReplyViewModel rule) => rule.ConfigChanged += OnReplyConfigChanged;

    [RelayCommand]
    private void AddReply()
    {
        var r = new AutoReplyViewModel { Name = $"reply{AutoReplies.Count + 1}" };
        AddReplyCore(r);
        AutoReplies.Add(r);
        SelectedReply = r;
        SaveReplies();
    }

    [RelayCommand(CanExecute = nameof(CanRemoveReply))]
    private void RemoveSelectedReply()
    {
        if (SelectedReply is null) return;
        SelectedReply.ConfigChanged -= OnReplyConfigChanged;
        AutoReplies.Remove(SelectedReply);
        SaveReplies();
    }

    private bool CanRemoveReply() => SelectedReply is not null;

    [RelayCommand]
    private void ClearReplies()
    {
        foreach (var r in AutoReplies)
            r.ConfigChanged -= OnReplyConfigChanged;
        AutoReplies.Clear();
        SaveReplies();
    }

    /// <summary>规则配置变更：自动保存 + HEX 合法性提示（非法/空回复的规则会静默不生效，需告知用户）。</summary>
    private void OnReplyConfigChanged(AutoReplyViewModel r)
    {
        SaveReplies();
        if (!r.Enabled) return;
        var bad = !Hex.TryParse(r.CommandHex, out _)
                  || !Hex.TryParse(r.MatchHex, out _)
                  || !Hex.TryParse(r.ReplyHex, out var rep)
                  || rep.Length == 0;
        if (bad)
            StatusText = $"应答规则[{r.Name}] HEX 非法或回复为空，该规则不生效";
    }

    // ---------- UI 设置持久化 ----------

    // 可选参数默认值：旧配置缺字段时按此处理。
    // 波形面板默认关闭（2026-09-03 用户要求）：启动不自动弹图表窗，用户按需勾选，勾选状态仍记忆
    private sealed record UiSettings(bool ShowFramesPanel, bool ShowWavePanel = false, bool WaveFollow = true);

    private static string UiSettingsPath
        => System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "ui_settings.json");

    private void LoadUiSettings()
    {
        try
        {
            if (File.Exists(UiSettingsPath))
            {
                var s = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(UiSettingsPath));
                if (s is not null)
                {
                    ShowFramesPanel = s.ShowFramesPanel;
                    ShowWavePanel = s.ShowWavePanel;
                    WaveFollow = s.WaveFollow;
                }
            }
        }
        catch
        {
            // 设置损坏时使用默认值
        }
    }

    private void SaveUiSettings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UiSettingsPath)!);
            File.WriteAllText(UiSettingsPath, JsonSerializer.Serialize(
                new UiSettings(ShowFramesPanel, ShowWavePanel, WaveFollow),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不影响功能
        }
    }

    partial void OnShowFramesPanelChanged(bool value) => SaveUiSettings();

    partial void OnShowWavePanelChanged(bool value) => SaveUiSettings();

    partial void OnWaveFollowChanged(bool value) => SaveUiSettings();

    [RelayCommand]
    private void ClearRx()
    {
        _lines.Clear();
        RxCount = 0;
        TxCount = 0;
        FrameOkCount = 0;
        FrameErrCount = 0;
        _parser?.Reset();
        _rateWindow.Clear();
        RxRateText = "0 B/s";
        lock (_plotLock)
        {
            foreach (var p in FieldPlots)
                p.Pts.Clear();
        }
        lock (_waveLock)
        {
            _rxWave.Clear();
            _txWave.Clear();
            _rxPrev = 1;
            _txPrev = 1;
        }
        _waveStart = DateTime.Now;
        RxRendered?.Invoke(this, new RxRender(RxRenderKind.Clear, string.Empty));
        WaveRendered?.Invoke(this, EventArgs.Empty);
        FieldPlotsRendered?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 日志 ----------

    /// <summary>选择日志保存位置；日志进行中则切换到新文件继续写。</summary>
    [RelayCommand]
    private void BrowseLog()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "选择日志保存位置",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
            FileName = System.IO.Path.GetFileName(LogFilePath),
        };
        if (dlg.ShowDialog() != true) return;

        LogFilePath = dlg.FileName;
        if (LogEnabled)
        {
            StartLogging();
            StatusText = $"日志写入中: {LogFilePath}";
        }
    }

    /// <summary>在资源管理器中定位日志文件：文件已生成则选中高亮，否则打开日志目录。</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(LogFilePath);
            if (File.Exists(LogFilePath))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{LogFilePath}\"")
                { UseShellExecute = true });
            }
            else if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                { UseShellExecute = true });
                StatusText = "日志目录已打开（勾选\"记录日志\"后开始生成日志文件）";
            }
            else
            {
                StatusText = $"日志目录不存在: {dir}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"打开目录失败: {ex.Message}";
        }
    }

    /// <summary>开启日志：打开文件并写会话头。</summary>
    private void StartLogging()
    {
        try
        {
            _logger.Open(LogFilePath);
            _logger.WriteLine($"===== Serial Tool 会话 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
        }
        catch (Exception ex)
        {
            LogEnabled = false;
            StatusText = $"日志开启失败: {ex.Message}";
        }
    }

    // ---------- 数据流 ----------

    /// <summary>读取线程回调：仅入队/喂解析器/记录波形，不做任何 UI 操作。</summary>
    private void OnDataReceived(object? sender, TimedData e)
    {
        Interlocked.Add(ref _pendingRxBytes, e.Bytes.Length);
        AppendWave(_rxWave, e.Bytes, e.Timestamp, ref _rxPrev); // 波形与解析/显示模式无关
        if (_parser != null)
        {
            // 帧解析模式：原始字节流进解析器，接收区只显示解出的帧
            _parser.Feed(e.Bytes);
            return;
        }
        _rxQueue.Enqueue(new RxItem(e.Timestamp, e.Bytes, IsTx: false));
    }

    /// <summary>UI 定时批量投递：高波特率下避免逐字节刷新；同时把新行落盘。
    /// 渲染走追加事件（不整体重置文本，保留用户滚动位置）。
    /// 统计在早退之前执行（空闲时速率归零、时长持续走）。</summary>
    private void FlushRx(object? sender, EventArgs e)
    {
        UpdateStats();
        ProcessDueReplies();
        if (_rxQueue.IsEmpty) return;

        var newItems = new List<RxItem>();
        while (_rxQueue.TryDequeue(out var item))
        {
            _lines.Add(item);
            newItems.Add(item);
        }
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);

        // 帧统计
        long ok = 0, err = 0;
        foreach (var item in newItems)
        {
            if (item.Frame is null) continue;
            if (item.Frame.Ok) ok++; else err++;
        }
        if (ok > 0) FrameOkCount += ok;
        if (err > 0) FrameErrCount += err;

        // 自动应答匹配：仅成功帧，首条命中规则出队一条应答（延迟由 ProcessDueReplies 调度）
        if (AutoReplies.Count > 0)
        {
            foreach (var item in newItems)
            {
                if (item.Frame is not { Ok: true } f) continue;
                MatchAutoReply(f);
            }
        }

        var sb = new System.Text.StringBuilder(newItems.Count * 32);
        foreach (var item in newItems)
        {
            var line = FormatLine(item);
            if (_logger.IsActive)
                _logger.WriteLine(line);
            if (PassFilter(item))
                sb.Append(line).Append('\n');
        }

        RxRendered?.Invoke(this, new RxRender(RxRenderKind.Append, sb.ToString()));
        WaveRendered?.Invoke(this, EventArgs.Empty);
        FieldPlotsRendered?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 状态栏统计 ----------

    /// <summary>每拍刷新：清空待计字节并入滑窗，算近 5 秒速率与连接时长。</summary>
    private void UpdateStats()
    {
        var pending = Interlocked.Exchange(ref _pendingRxBytes, 0);
        if (pending > 0)
        {
            RxCount += pending;
            _rateWindow.Enqueue((DateTime.Now, pending));
        }

        var now = DateTime.Now;
        while (_rateWindow.Count > 0 && _rateWindow.Peek().T < now.AddSeconds(-RateWindowSec))
            _rateWindow.Dequeue();

        var rate = 0.0;
        if (_rateWindow.Count > 0)
        {
            var bytes = 0L;
            foreach (var w in _rateWindow) bytes += w.Bytes;
            var span = Math.Max(1.0, (now - _rateWindow.Peek().T).TotalSeconds);
            rate = bytes / span;
        }
        RxRateText = rate < 1024
            ? $"{rate:F0} B/s"
            : $"{rate / 1024:F1} kB/s";

        ElapsedText = _connectedSince is { } t
            ? (now - t).ToString(@"hh\:mm\:ss")
            : string.Empty;

        // 输入引脚轮询（50ms 一拍，UI 线程；TCP 模式跳过）
        if (IsPortOpen && IsSerial)
        {
            var sig = _serialBackend.Signals;
            CtsOn = sig.Cts;
            DsrOn = sig.Dsr;
        }
    }

    /// <summary>视图过滤判定：关 = 全过；开 = HEX 子串或显示文本子串命中。</summary>
    private bool PassFilter(RxItem it)
        => !FilterEnabled || RxFilter.IsMatch(RxFilterText, it.Bytes);

    // ---------- 自动应答调度（UI 线程：FlushRx 每 50ms 一拍） ----------

    /// <summary>对一帧跑规则匹配：首条命中 → 计数、Once 禁用、按延迟入待发队列。</summary>
    private void MatchAutoReply(ParsedFrame f)
    {
        foreach (var r in AutoReplies)
        {
            if (!r.Enabled || !AutoReplyMatcher.IsMatch(r.Snapshot, f)) continue;
            if (!Hex.TryParse(r.ReplyHex, out var bytes) || bytes.Length == 0) return; // 快照已验，双保险
            r.HitCount++;
            if (r.Once)
                r.Enabled = false; // 触发 ConfigChanged → 持久化禁用状态
            _pendingReplies.Add((DateTime.Now.AddMilliseconds(Math.Max(0, r.DelayMs)),
                bytes, $"应答[{r.Name}]"));
            return; // 首条命中即止
        }
    }

    /// <summary>发出到期的应答（倒序删除避免索引错位）。</summary>
    private void ProcessDueReplies()
    {
        if (_pendingReplies.Count == 0) return;
        var now = DateTime.Now;
        for (var i = _pendingReplies.Count - 1; i >= 0; i--)
        {
            if (_pendingReplies[i].Due > now) continue;
            var r = _pendingReplies[i];
            _pendingReplies.RemoveAt(i);
            WriteBytes(r.Bytes, r.Label, silent: true, tag: "⇄ ");
        }
    }

    // ---------- 逻辑分析仪式波形（按字节 + 波特率逐位重建） ----------

    /// <summary>当前位宽（秒）：串口按所选波特率，TCP 按标称值。</summary>
    private double BitDuration => 1.0 / (IsSerial ? Math.Max(SelectedBaud, 1) : TcpNominalBaud);

    /// <summary>把一段字节展开成 UART 位序列跳变（起始位0 + 8数据位LSB在前 + 停止位1）。</summary>
    private void AppendWave(List<WavePt> buf, byte[] data, DateTime t0, ref double prev)
    {
        var bitDur = BitDuration;
        var t = (t0 - _waveStart).TotalSeconds;
        lock (_waveLock)
        {
            foreach (var b in data)
            {
                for (var bit = 0; bit < 10; bit++)
                {
                    int level = bit == 0 ? 0          // 起始位
                               : bit == 9 ? 1          // 停止位
                               : (b >> (bit - 1)) & 1; // 数据位 LSB 在前
                    if (level != prev)
                    {
                        buf.Add(new WavePt(t, level));
                        prev = level;
                    }
                    t += bitDur;
                }
            }
            if (buf.Count > MaxWavePoints)
                buf.RemoveRange(0, buf.Count - WaveTrimKeep);
        }
    }

    /// <summary>RX 波形快照（UI 线程拉取）。</summary>
    public (double[] Xs, double[] Ys, double Prev) RxWaveSnapshot()
    {
        lock (_waveLock)
        {
            var xs = new double[_rxWave.Count];
            var ys = new double[_rxWave.Count];
            for (var i = 0; i < _rxWave.Count; i++) { xs[i] = _rxWave[i].T; ys[i] = _rxWave[i].Y; }
            return (xs, ys, _rxPrev);
        }
    }

    /// <summary>TX 波形快照（UI 线程拉取）。</summary>
    public (double[] Xs, double[] Ys, double Prev) TxWaveSnapshot()
    {
        lock (_waveLock)
        {
            var xs = new double[_txWave.Count];
            var ys = new double[_txWave.Count];
            for (var i = 0; i < _txWave.Count; i++) { xs[i] = _txWave[i].T; ys[i] = _txWave[i].Y; }
            return (xs, ys, _txPrev);
        }
    }

    /// <summary>单行格式化：[时间戳] 方向 数据（显示与日志共用）。</summary>
    private string FormatLine(RxItem item)
    {
        if (item.Frame is { } f)
            return FormatFrameLine(item.Ts, f);

        var sb = new System.Text.StringBuilder(48 + item.Bytes.Length * 3);
        if (ShowTimestamp)
            sb.Append('[').Append(item.Ts.ToString("HH:mm:ss.fff")).Append("] ");
        sb.Append(item.Tag ?? (item.IsTx ? "→ " : "← ")); // 自动应答回显用 ⇄ 区分手动发送
        sb.Append(ShowHex ? Hex.Encode(item.Bytes) : TextDecode.ToDisplay(item.Bytes));
        return sb.ToString();
    }

    /// <summary>帧行格式化：✓/✗ [模板] 帧头 |命令| 数据 | 校验（+错误原因）。</summary>
    private string FormatFrameLine(DateTime ts, ParsedFrame f)
    {
        var sb = new System.Text.StringBuilder(64 + f.Raw.Length * 3);
        if (ShowTimestamp)
            sb.Append('[').Append(ts.ToString("HH:mm:ss.fff")).Append("] ");
        sb.Append(f.Ok ? "✓ " : "✗ ");
        if (f.TemplateName.Length > 0)
            sb.Append('[').Append(f.TemplateName).Append("] ");

        var headLen = f.CommandOffset >= 0 ? f.CommandOffset : f.PayloadOffset;
        sb.Append(Hex.Encode(f.Raw.AsSpan(0, headLen)));
        if (f.CommandOffset >= 0)
            sb.Append(" |").Append(Hex.Encode(f.Raw.AsSpan(f.CommandOffset, f.PayloadOffset - f.CommandOffset))).Append('|');
        sb.Append(' ').Append(Hex.Encode(f.Raw.AsSpan(f.PayloadOffset, f.PayloadLength)));
        var tail = Hex.Encode(f.Raw.AsSpan(f.PayloadOffset + f.PayloadLength));
        if (tail.Length > 0)
            sb.Append(" | ").Append(tail);
        if (!f.Ok)
            sb.Append("   ← ").Append(f.Error);
        return sb.ToString();
    }

    /// <summary>全量文本（显示模式切换/过滤变化时全量重绘用；应用当前过滤）。</summary>
    private string BuildFullText()
    {
        var sb = new System.Text.StringBuilder(_lines.Count * 32);
        foreach (var item in _lines)
            if (PassFilter(item))
                sb.Append(FormatLine(item)).Append('\n');
        return sb.ToString();
    }

    private void OnBackendError(object? sender, string msg)
        => Dispatch(() =>
        {
            _active = null;
            IsPortOpen = false;
            OnLinkClosed();
            StatusText = msg;
        });

    private static void Dispatch(Action action)
        => Application.Current?.Dispatcher.BeginInvoke(action);

    // ---------- 命令状态联动 ----------

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        switch (e.PropertyName)
        {
            case nameof(IsPortOpen):
                TogglePortCommand.NotifyCanExecuteChanged();
                SendCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanControlPins));
                break;
            case nameof(SelectedDevice) or nameof(TcpHost) or nameof(TcpPort):
                TogglePortCommand.NotifyCanExecuteChanged();
                break;
            case nameof(TxInput):
                SendCommand.NotifyCanExecuteChanged();
                break;
            case nameof(SelectedFrame):
                RemoveSelectedFrameCommand.NotifyCanExecuteChanged();
                break;
            case nameof(SelectedPlot):
                RemoveSelectedPlotCommand.NotifyCanExecuteChanged();
                break;
            case nameof(SelectedReply):
                RemoveSelectedReplyCommand.NotifyCanExecuteChanged();
                break;
            case nameof(ShowHex) or nameof(ShowTimestamp) or nameof(RxFilterText) or nameof(FilterEnabled):
                // 显示模式/过滤切换：全量重绘，视图恢复原滚动位置
                RxRendered?.Invoke(this, new RxRender(RxRenderKind.Full, BuildFullText()));
                break;
            case nameof(LogEnabled):
                if (LogEnabled)
                {
                    StartLogging();
                    if (LogEnabled) // 开启成功（失败时已复位并提示）
                        StatusText = $"日志写入中: {LogFilePath}";
                }
                else
                {
                    _logger.Close();
                    StatusText = "日志已停止";
                }
                break;
        }
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _serialBackend.Dispose();
        _tcpBackend.Dispose();
        _logger.Dispose();
    }
}
