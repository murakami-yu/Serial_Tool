using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SerialTool.App.Services;
using SerialTool.Core;
using SerialTool.Core.Framing;
using SerialTool.Backends;
using SerialTool.Backends.Serial;
using SerialTool.Backends.Ssh;
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

/// <summary>一段同方向显示文本（连续同方向行合并，减少段落着色开销；段内行尾带 '\n'）。</summary>
public sealed record RxSeg(string Text, bool IsTx);

public sealed record RxRender(RxRenderKind Kind, IReadOnlyList<RxSeg> Segments);

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
    private readonly SshBackend _sshBackend = new();

    /// <summary>SSH 已知主机库（TOFU）：Config/known_hosts.json。</summary>
    private readonly KnownHostsStore _knownHosts =
        new(System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "known_hosts.json"));

    // 终端网格最近尺寸（SSH 初始 PTY 与 resize 通知用；未开过终端按 80×24）
    private int _termCols = 80, _termRows = 24;

    /// <summary>当前活动连接（串口或 TCP），未连接为 null。</summary>
    private IBusBackend? _active;

    /// <summary>当前连接的人类可读描述（如 "COM3 @ 115200"），运行日志断开时引用。</summary>
    private string _activeDesc = string.Empty;

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

    /// <summary>连接方式：0 = 串口，1 = TCP，2 = SSH。</summary>
    [ObservableProperty]
    private int _connTypeIndex;

    [ObservableProperty]
    private string _tcpHost = "192.168.1.100";

    [ObservableProperty]
    private int _tcpPort = 8899;

    public bool IsSerial => ConnTypeIndex == 0;
    public bool IsTcp => ConnTypeIndex == 1;
    public bool IsSsh => ConnTypeIndex == 2;

    // ---------- SSH 连接参数（凭据不持久化，其余入 ui_settings.json） ----------

    [ObservableProperty]
    private string _sshHost = "192.168.1.100";

    [ObservableProperty]
    private int _sshPort = 22;

    [ObservableProperty]
    private string _sshUser = "root";

    /// <summary>认证方式：0 = 密码，1 = 私钥文件。</summary>
    [ObservableProperty]
    private int _sshAuthIndex;

    [ObservableProperty]
    private string _sshKeyPath = "";

    /// <summary>登录密码（PasswordBox 回写，不持久化不落盘）。</summary>
    public string SshPassword { get; set; } = "";

    /// <summary>私钥口令（PasswordBox 回写，不持久化不落盘）。</summary>
    public string SshKeyPassphrase { get; set; } = "";

    /// <summary>SSH 认证明细行可见性：仅在 SSH 模式下按认证方式二选一（曾漏 IsSsh 门槛，
    /// 串口/TCP 模式「密码」行也常驻，把底部条「连接」按钮挤出可视区）。</summary>
    public bool IsSshPasswordAuth => IsSsh && SshAuthIndex != 1;
    public bool IsSshKeyAuth => IsSsh && SshAuthIndex == 1;

    /// <summary>生效波特率文本（预设或自定义值，持久化到 Config/ui_settings.json）；
    /// 主框不直接键入，自定义经下拉「自定义…」对话框。</summary>
    [ObservableProperty]
    private string _baudText = "115200";

    /// <summary>下拉当前选中项；选中即回写 BaudText 保证回显与持久化。
    /// 选中「自定义…」时弹对话框，按结果插入列表选中或回退原选择。</summary>
    [ObservableProperty]
    private string? _selectedBaudItem;

    /// <summary>解析波特率输入：合法正整数返回值；非法返回 0（连接时报错，波形按标称位宽兜底）。</summary>
    public int SelectedBaud =>
        int.TryParse(BaudText.Trim(), out var b) && b > 0 ? b : 0;

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

    /// <summary>图表窗口是否打开（独立顶层窗口，持久化）。</summary>
    [ObservableProperty]
    private bool _showWavePanel = true;

    /// <summary>终端窗口是否打开（VT100/xterm 终端仿真独立窗口，持久化；默认关闭）。</summary>
    [ObservableProperty]
    private bool _showTerminalPanel;

    // ---------- 接收区 TX/RX 行颜色（持久化；渲染用冻结画刷缓存） ----------

    public const string DefaultTxColor = "#0078D7"; // 主题强调蓝
    public const string DefaultRxColor = "#1E1E1E"; // 正文字色（接收视觉不变）

    /// <summary>发送行颜色（HEX 字符串，便于 JSON 持久化；→ 手动 / ⇄ 自动应答）。</summary>
    [ObservableProperty]
    private string _txColorHex = DefaultTxColor;

    /// <summary>接收行颜色（HEX 字符串；← 数据 / ✓✗ 帧）。</summary>
    [ObservableProperty]
    private string _rxColorHex = DefaultRxColor;

    /// <summary>发送行渲染画刷（冻结缓存，追加时按引用共享）。</summary>
    public SolidColorBrush TxBrush { get; private set; } = MakeBrush(DefaultTxColor, DefaultTxColor);

    /// <summary>接收行渲染画刷（冻结缓存）。</summary>
    public SolidColorBrush RxBrush { get; private set; } = MakeBrush(DefaultRxColor, DefaultRxColor);

    partial void OnTxColorHexChanged(string value)
    {
        TxBrush = MakeBrush(value, DefaultTxColor);
        AppLog.Info($"发送行颜色变更：{value}");
        SaveUiSettings();
    }

    partial void OnRxColorHexChanged(string value)
    {
        RxBrush = MakeBrush(value, DefaultRxColor);
        AppLog.Info($"接收行颜色变更：{value}");
        SaveUiSettings();
    }

    /// <summary>HEX → 冻结画刷；非法值回退到对应默认色（手改配置文件的兜底）。</summary>
    private static SolidColorBrush MakeBrush(string hex, string fallback)
    {
        SolidColorBrush b;
        try
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch
        {
            b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        }
        b.Freeze();
        return b;
    }

    /// <summary>HEX 字符串合法性（配置加载时校验）。</summary>
    private static bool IsValidHex(string hex)
    {
        try
        {
            ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>字体族合法：系统已安装字体名单内（大小写不敏感），防手改配置的坏值。</summary>
    private static bool IsKnownFont(string name)
        => System.Windows.Media.Fonts.SystemFontFamilies.Any(f => string.Equals(f.Source, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>字号合法：可解析且 6~72。</summary>
    private static bool IsValidFontSize(string text)
        => double.TryParse(text.Trim(), out var v) && v is >= 6 and <= 72;

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

    /// <summary>主发送区定时发送开关（持久化；勾选后按 TxPeriodMs 周期自动重发输入框内容）。</summary>
    [ObservableProperty]
    private bool _txCyclic;

    /// <summary>主发送区定时发送周期 ms（持久化；实际周期不小于循环调度粒度 50ms）。</summary>
    [ObservableProperty]
    private int _txPeriodMs = 1000;

    /// <summary>主发送区下一次定时发送到期时刻（仅 UI 线程；勾选后先立即发一帧再按周期排程）。</summary>
    private DateTime _txNextDue;

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

    /// <summary>波特率下拉末尾的「自定义…」选项：选中弹输入对话框（主框不自由输入）。</summary>
    private const string CustomBaudLabel = "自定义…";

    /// <summary>波特率下拉项：常用预设 + 已持久化的自定义值 + 末尾「自定义…」。项为字符串保证回显。</summary>
    public ObservableCollection<string> BaudRates { get; } = new()
    {
        "300", "600", "1200", "2400", "4800", "9600", "14400", "19200", "28800", "38400",
        "57600", "115200", "128000", "230400", "256000", "460800", "500000", "576000",
        "750000", "921600", "1000000", "1500000", "2000000",
        CustomBaudLabel,
    };

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
        _sshBackend.DataReceived += OnDataReceived;
        _sshBackend.ErrorOccurred += OnBackendError;
        _sshBackend.HostKeyVerifying += OnSshHostKeyVerifying;

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
        // 外观单例属性变化（外观设置窗改色）→ 即存（与 TxColorHex 等同一"变化即落盘"惯例）
        // 订阅在 LoadUiSettings 之后添加：启动期批量回填不会进操作日志；主题预设一套 13 项逐条记（用户一次点击的真实后果）
        Services.Appearance.Instance.PropertyChanged += (_, a) =>
        {
            SaveUiSettings();
            var v = a.PropertyName is null ? null
                : Services.Appearance.Instance.GetType().GetProperty(a.PropertyName)
                    ?.GetValue(Services.Appearance.Instance)?.ToString();
            AppLog.Info($"外观变更：{a.PropertyName} = {v}");
        };
        SyncBaudSelection();
        LoadTemplates();
        _ = LoadPortsAsync();
        LoadFrames();
        LoadPlots();
        LoadReplies();
    }

    // ---------- 帧解析联动 ----------

    partial void OnParseEnabledChanged(bool value)
    {
        AppLog.Info($"帧解析：{(value ? "开启" : "关闭")}");
        RebuildParser();
    }

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
            AppLog.Info($"帧解析仲裁就绪：{active.Count} 个模板（{string.Join("、", active.Select(t => t.Name))}）");
            StatusText = $"帧解析开启: {active.Count} 个模板并行仲裁";
        }
        catch (Exception ex)
        {
            ParseEnabled = false; // 触发本方法重入，清理解析器
            AppLog.Warn($"帧解析器构建失败（已自动关闭解析）：{string.Join("、", active.Select(t => t.Name))}", ex);
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
        AppLog.Info("打开模板编辑器");
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
        catch (Exception ex)
        {
            // 配置损坏时使用默认模板
            AppLog.Warn($"模板配置加载失败，使用默认模板：{TemplatesPath}", ex);
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
            AppLog.Warn($"模板保存失败：{TemplatesPath}", ex);
            StatusText = $"模板保存失败: {ex.Message}";
        }
    }

    // ---------- 连接方式联动 ----------

    partial void OnSshAuthIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsSshPasswordAuth));
        OnPropertyChanged(nameof(IsSshKeyAuth));
        SaveUiSettings();
    }

    partial void OnSshHostChanged(string value) => SaveUiSettings();

    partial void OnSshPortChanged(int value) => SaveUiSettings();

    partial void OnSshUserChanged(string value) => SaveUiSettings();

    partial void OnSshKeyPathChanged(string value) => SaveUiSettings();

    partial void OnConnTypeIndexChanged(int value)
    {
        AppLog.Info($"切换连接方式 → {(value == 0 ? "串口" : value == 1 ? "TCP" : "SSH")}");
        OnPropertyChanged(nameof(IsSerial));
        OnPropertyChanged(nameof(IsTcp));
        OnPropertyChanged(nameof(IsSsh));
        OnPropertyChanged(nameof(IsSshPasswordAuth));
        OnPropertyChanged(nameof(IsSshKeyAuth));
        OnPropertyChanged(nameof(CanControlPins));
        TogglePortCommand.NotifyCanExecuteChanged();
        // 切换连接方式时若已连接则先断开
        if (IsPortOpen)
        {
            AppLog.Info($"切换连接方式，断开 {_activeDesc}");
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
        IReadOnlyList<DeviceInfo> items;
        try
        {
            items = await Task.Run(_serialBackend.Scan);
        }
        catch (Exception ex)
        {
            AppLog.Warn("串口扫描失败", ex);
            StatusText = $"串口扫描失败: {ex.Message}";
            return;
        }
        PortItems = new ObservableCollection<DeviceInfo>(items);
        if (SelectedDevice is null || items.All(d => d.Id != SelectedDevice.Id))
            SelectedDevice = items.FirstOrDefault();
        AppLog.Info(items.Count > 0
            ? $"串口扫描：发现 {items.Count} 个（{string.Join(", ", items.Select(d => d.Id))}）"
            : "串口扫描：未发现串口");
        StatusText = items.Count > 0
            ? $"发现 {items.Count} 个串口"
            : "未发现串口（插入设备后点刷新）";
    }

    private async Task LoadPortsAsync() => await RefreshPorts();

    [RelayCommand(CanExecute = nameof(CanTogglePort))]
    private async Task TogglePortAsync()
    {
        if (IsPortOpen)
        {
            AppLog.Info($"用户断开 {_activeDesc}");
            _active?.Close();
            _active = null;
            IsPortOpen = false;
            OnLinkClosed();
            StatusText = "已断开";
            return;
        }

        // 连接目标描述：成功/失败都进运行日志（不含凭据）
        var targetDesc = IsSerial
            ? $"串口 {SelectedDevice?.Id} @ {BaudText.Trim()}"
            : IsTcp
                ? $"TCP {TcpHost.Trim()}:{TcpPort}"
                : $"SSH {SshUser.Trim()}@{SshHost.Trim()}:{SshPort}（{(SshAuthIndex == 1 ? "私钥" : "密码")}认证）";
        try
        {
            if (IsSerial)
            {
                var baud = SelectedBaud;
                if (baud <= 0)
                {
                    AppLog.Warn($"连接被拒绝：波特率非法 \"{BaudText.Trim()}\"");
                    StatusText = "波特率非法：请输入正整数（如 115200），或从下拉列表选择";
                    return;
                }
                _serialBackend.Open(new SerialPortConfig(
                    SelectedDevice!.Id, baud, SelectedDataBits,
                    (SerialParity)SelectedParityIndex,
                    (SerialStopBits)SelectedStopBitsIndex));
                _active = _serialBackend;
                StatusText = $"已打开 {SelectedDevice.Id} @ {baud}";
            }
            else if (IsTcp)
            {
                _tcpBackend.Open(new TcpConfig(TcpHost.Trim(), TcpPort));
                _active = _tcpBackend;
                StatusText = $"TCP {TcpHost.Trim()}:{TcpPort} 已连接";
            }
            else
            {
                // SSH：连接移至后台线程执行——SSH.NET 2026 在工作线程（非调用线程）回调
                // HostKeyReceived，指纹弹窗须回 UI 线程；若 UI 线程阻塞在 Connect 内，
                // 弹窗封送会死锁 / 工作线程直接建 Window 抛 STA 异常。
                // 后台执行 + UI 空闲 → 同步弹窗裁决可行（10s 超时在库内）。
                var cfg = new SshConfig(SshHost.Trim(), SshPort, SshUser.Trim(),
                    SshAuthIndex == 0 ? SshPassword : null,
                    SshAuthIndex == 1 ? SshKeyPath : null,
                    SshAuthIndex == 1 ? SshKeyPassphrase : null,
                    _termCols, _termRows);
                StatusText = $"SSH {SshUser.Trim()}@{SshHost.Trim()}:{SshPort} 连接中…";
                await Task.Run(() => _sshBackend.Open(cfg));
                _active = _sshBackend;
                StatusText = $"SSH {SshUser.Trim()}@{SshHost.Trim()}:{SshPort} 已连接";
            }
            _activeDesc = targetDesc;
            AppLog.Info($"连接成功：{targetDesc}");
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
            AppLog.Warn($"连接失败：{targetDesc}", ex);
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
               : IsTcp
                   ? !string.IsNullOrWhiteSpace(TcpHost) && TcpPort is > 0 and <= 65535
                   : !string.IsNullOrWhiteSpace(SshHost) && SshPort is > 0 and <= 65535
                     && !string.IsNullOrWhiteSpace(SshUser));

    /// <summary>手动发送（「发送」按钮 / 输入框 Enter）。</summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send() => SendMain(silent: false);

    /// <summary>主发送区实际发送：HEX 解析失败或内容为空返回 false（定时调度据此跳过本轮）。</summary>
    private bool SendMain(bool silent)
    {
        var text = TxInput;
        if (TxHexMode)
        {
            if (!Hex.TryParse(text, out var bytes))
            {
                if (!silent) StatusText = "HEX 格式错误：需要偶数个合法十六进制字符";
                return false;
            }
            if (bytes.Length == 0) return false;
            WriteBytes(bytes, silent: silent);
        }
        else
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length == 0) return false;
            WriteBytes(bytes, silent: silent);
        }
        return true;
    }

    private bool CanSend() => IsPortOpen && !string.IsNullOrWhiteSpace(TxInput);

    /// <summary>主机指纹确认弹窗请求（同步：连接挂起等待回写 Accepted）。MainWindow 订阅弹窗。</summary>
    public event EventHandler<SshHostKeyChallenge>? HostKeyChallenge;

    /// <summary>SSH 主机指纹校验（TOFU）：已信任直通；首次/变更弹窗裁决，接受即写 known_hosts。
    /// 主连接与终端窗多会话共用同一入口。SSH.NET 2026 在工作线程回调本事件，
    /// 弹窗前须封送回 UI 线程（Open 已在后台线程执行，UI 空闲，同步 Invoke 安全）。</summary>
    private void OnSshHostKeyVerifying(object? sender, SshHostKeyChallenge e)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
            VerifyHostKey(e);
        else
            d.Invoke(() => VerifyHostKey(e));
    }

    /// <summary>同步校验并弹窗（UI 线程调用）：返回是否信任。</summary>
    public bool VerifyHostKey(SshHostKeyChallenge e)
    {
        var result = _knownHosts.Verify(e.Host, e.Port, e.Algorithm, e.FingerprintSha256);
        if (result == KnownHostResult.Trusted)
        {
            e.Accepted = true;
            return true;
        }
        e.Changed = result == KnownHostResult.Changed;
        // 安全相关裁决全程留痕（含指纹，便于核对是否中间人替换）
        AppLog.Warn($"SSH 主机指纹待裁决：{e.Host}:{e.Port} {e.Algorithm} " +
                    $"{(e.Changed ? "【指纹变更】" : "【首次连接】")} SHA256={e.FingerprintSha256}");
        HostKeyChallenge?.Invoke(this, e);
        if (e.Accepted)
        {
            _knownHosts.Trust(e.Host, e.Port, e.Algorithm, e.FingerprintSha256);
            AppLog.Info($"SSH 主机指纹已信任：{e.Host}:{e.Port} {e.Algorithm}");
        }
        else
        {
            AppLog.Warn($"SSH 主机指纹被拒绝：{e.Host}:{e.Port} {e.Algorithm}");
        }
        return e.Accepted;
    }

    /// <summary>终端网格尺寸变化（TerminalView.Resized）→ SSH 通道窗口变更；记录最近尺寸供下次连接初始 PTY。</summary>
    public void NotifyTerminalResized(int cols, int rows)
    {
        _termCols = cols;
        _termRows = rows;
        if (ReferenceEquals(_active, _sshBackend) && _sshBackend.IsOpen)
            _sshBackend.ResizeTerminal(cols, rows);
    }

    /// <summary>终端输入原始字节发送：不回显进接收区行缓冲、不写会话日志
    /// （终端视图已有对端回显，避免逐键刷接收区），仅计 TX 统计与 TX 波形（与主发送同链路）。</summary>
    public void SendTerminalBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || _active is null) return;
        try
        {
            _active.Write(bytes);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"终端发送失败（{bytes.Length} 字节）", ex);
            StatusText = $"发送失败: {ex.Message}";
            return;
        }
        AppendWave(_txWave, bytes, DateTime.Now, ref _txPrev);
        TxCount += bytes.Length;
    }

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
            AppLog.Warn($"发送失败（{bytes.Length} 字节{(label is null ? "" : $"，{label}")}）", ex);
            StatusText = $"发送失败: {ex.Message}";
            return;
        }
        _rxQueue.Enqueue(new RxItem(DateTime.Now, bytes, IsTx: true, Tag: tag));
        AppendWave(_txWave, bytes, DateTime.Now, ref _txPrev);
        TxCount += bytes.Length;
        if (!silent)
        {
            // 操作日志只记行为与字节数，不记数据内容（数据由用户手开的会话日志负责）
            AppLog.Info(label is null ? $"手动发送 {bytes.Length} 字节" : $"发送{label}（{bytes.Length} 字节）");
            StatusText = label is null ? $"已发送 {bytes.Length} 字节" : $"已发送 {label}（{bytes.Length} 字节）";
        }
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

    /// <summary>循环调度：周期到点的帧发送。实际周期不小于调度粒度。
    /// 主发送区「定时发送」复用本节拍（勾选时已立即发过首帧，这里按周期续发）。</summary>
    private void CyclicTick(object? sender, EventArgs e)
    {
        if (!IsPortOpen) return;
        var now = DateTime.Now;

        // 主发送区定时发送：内容为空/HEX 非法时跳过本轮，下周期重试（不打断节奏、不刷状态栏）
        if (TxCyclic && now >= _txNextDue)
        {
            _txNextDue = now.AddMilliseconds(Math.Max(TxPeriodMs, CyclicTickMs));
            SendMain(silent: true);
        }

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
        if (!_seeding) AppLog.Info($"新增发送帧 #{frame.Index}（共 {SendFrames.Count} 条）");
    }

    /// <summary>启动种子生成期标记：LoadFrames 空配置时的 8 条种子帧非用户操作，不入操作日志。</summary>
    private bool _seeding;

    [RelayCommand(CanExecute = nameof(CanRemoveFrame))]
    private void RemoveSelectedFrame()
    {
        if (SelectedFrame is null) return;
        AppLog.Info($"删除发送帧 #{SelectedFrame.Index}（剩 {SendFrames.Count - 1} 条）");
        SelectedFrame.PropertyChanged -= OnFramePropertyChanged;
        SendFrames.Remove(SelectedFrame);
    }

    private bool CanRemoveFrame() => SelectedFrame is not null;

    [RelayCommand]
    private void ClearFrames()
    {
        AppLog.Info($"清空发送帧列表（原 {SendFrames.Count} 条）");
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
        catch (Exception ex)
        {
            // 配置损坏时回退到默认空帧
            AppLog.Warn($"帧配置加载失败，回退默认空帧：{FramesConfigPath}", ex);
        }
        if (SendFrames.Count == 0)
        {
            _seeding = true;
            for (var i = 0; i < 8; i++)
                AddFrame();
            _seeding = false;
        }
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
            AppLog.Warn($"帧配置保存失败：{FramesConfigPath}", ex);
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
        catch (Exception ex)
        {
            // 配置损坏时回退到默认样例
            AppLog.Warn($"曲线配置加载失败，回退默认样例：{PlotsConfigPath}", ex);
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
            AppLog.Warn($"曲线配置保存失败：{PlotsConfigPath}", ex);
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
        AppLog.Info($"新增字段曲线 curve{n}（共 {FieldPlots.Count} 条）");
    }

    [RelayCommand(CanExecute = nameof(CanRemovePlot))]
    private void RemoveSelectedPlot()
    {
        if (SelectedPlot is null) return;
        AppLog.Info($"删除字段曲线 {SelectedPlot.Name}（剩 {FieldPlots.Count - 1} 条）");
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
        AppLog.Info($"清空字段曲线（原 {FieldPlots.Count} 条）");
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
        catch (Exception ex)
        {
            // 配置损坏时回退到空规则
            AppLog.Warn($"应答配置加载失败，回退空规则：{RepliesConfigPath}", ex);
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
            AppLog.Warn($"应答配置保存失败：{RepliesConfigPath}", ex);
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
        AppLog.Info($"新增应答规则 {r.Name}（共 {AutoReplies.Count} 条）");
    }

    [RelayCommand(CanExecute = nameof(CanRemoveReply))]
    private void RemoveSelectedReply()
    {
        if (SelectedReply is null) return;
        AppLog.Info($"删除应答规则 {SelectedReply.Name}（剩 {AutoReplies.Count - 1} 条）");
        SelectedReply.ConfigChanged -= OnReplyConfigChanged;
        AutoReplies.Remove(SelectedReply);
        SaveReplies();
    }

    private bool CanRemoveReply() => SelectedReply is not null;

    [RelayCommand]
    private void ClearReplies()
    {
        AppLog.Info($"清空应答规则（原 {AutoReplies.Count} 条）");
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
    // 波形面板默认关闭（2026-09-03 用户要求）：启动不自动弹图表窗，用户按需打开，打开状态仍记忆
    private sealed record UiSettings(bool ShowFramesPanel, bool ShowWavePanel = false, bool WaveFollow = true,
        string TxColor = "#0078D7", string RxColor = "#1E1E1E", string Baud = "115200",
        bool TxCyclic = false, int TxPeriodMs = 1000, bool ShowTerminalPanel = false,
        string SshHost = "192.168.1.100", int SshPort = 22, string SshUser = "root",
        int SshAuthIndex = 0, string SshKeyPath = "",
        string MainBg = Services.Appearance.DefaultMainBg,
        string TerminalBg = Services.Appearance.DefaultTerminalBg,
        string ChartBg = Services.Appearance.DefaultChartBg,
        string TemplateBg = Services.Appearance.DefaultTemplateBg,
        string TermContentBg = Services.Appearance.DefaultTermContentBg,
        string Panel = Services.Appearance.DefaultPanel,
        string ButtonBg = Services.Appearance.DefaultButtonBg,
        string Border = Services.Appearance.DefaultBorder,
        string Text = Services.Appearance.DefaultText,
        string Muted = Services.Appearance.DefaultMuted,
        string Accent = Services.Appearance.DefaultAccent,
        string Hover = Services.Appearance.DefaultHover,
        string Selected = Services.Appearance.DefaultSelected,
        string UiFontFamily = "", string UiFontSize = "");

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
                    ShowTerminalPanel = s.ShowTerminalPanel;
                    WaveFollow = s.WaveFollow;
                    // 颜色合法性校验：手改坏值按默认色启动
                    if (s.TxColor is { } tc && IsValidHex(tc)) TxColorHex = tc;
                    if (s.RxColor is { } rc && IsValidHex(rc)) RxColorHex = rc;
                    // 波特率（含自定义值）：坏值按默认 115200 启动
                    if (s.Baud is { } bd && int.TryParse(bd.Trim(), out var bv) && bv > 0) BaudText = bd.Trim();
                    // 主发送区定时发送：周期坏值按默认 1000ms 启动
                    TxCyclic = s.TxCyclic;
                    if (s.TxPeriodMs > 0) TxPeriodMs = s.TxPeriodMs;
                    // SSH 参数（凭据不落盘，这里只回填主机/端口/用户名/认证方式/私钥路径）
                    if (!string.IsNullOrWhiteSpace(s.SshHost)) SshHost = s.SshHost;
                    if (s.SshPort is > 0 and <= 65535) SshPort = s.SshPort;
                    if (!string.IsNullOrWhiteSpace(s.SshUser)) SshUser = s.SshUser;
                    if (s.SshAuthIndex is 0 or 1) SshAuthIndex = s.SshAuthIndex;
                    SshKeyPath = s.SshKeyPath ?? "";
                    // 外观（各窗口背景色）：坏值按默认色启动，写入单例供全窗口绑定
                    var ap = Services.Appearance.Instance;
                    if (s.MainBg is { } mb && IsValidHex(mb)) ap.MainBgHex = mb;
                    if (s.TerminalBg is { } tb && IsValidHex(tb)) ap.TerminalBgHex = tb;
                    if (s.ChartBg is { } cb && IsValidHex(cb)) ap.ChartBgHex = cb;
                    if (s.TemplateBg is { } pb && IsValidHex(pb)) ap.TemplateBgHex = pb;
                    if (s.TermContentBg is { } eb && IsValidHex(eb)) ap.TermContentBgHex = eb;
                    // 控件级配色（全局）
                    if (s.Panel is { } pc && IsValidHex(pc)) ap.PanelHex = pc;
                    if (s.ButtonBg is { } bb && IsValidHex(bb)) ap.ButtonBgHex = bb;
                    if (s.Border is { } bc && IsValidHex(bc)) ap.BorderHex = bc;
                    if (s.Text is { } tx && IsValidHex(tx)) ap.TextHex = tx;
                    if (s.Muted is { } mu && IsValidHex(mu)) ap.MutedHex = mu;
                    if (s.Accent is { } ac && IsValidHex(ac)) ap.AccentHex = ac;
                    if (s.Hover is { } hv && IsValidHex(hv)) ap.HoverHex = hv;
                    if (s.Selected is { } se && IsValidHex(se)) ap.SelectedHex = se;
                    // 界面字体（全局）：字体族须为系统已安装（防手改坏值），字号 6~72；坏值按默认（跟随系统）启动
                    if (!string.IsNullOrWhiteSpace(s.UiFontFamily) && IsKnownFont(s.UiFontFamily.Trim()))
                        ap.UiFontFamily = s.UiFontFamily.Trim();
                    if (!string.IsNullOrWhiteSpace(s.UiFontSize) && IsValidFontSize(s.UiFontSize))
                        ap.UiFontSize = s.UiFontSize.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            // 设置损坏时使用默认值
            AppLog.Warn($"UI 设置加载失败，使用默认值：{UiSettingsPath}", ex);
        }
    }

    private void SaveUiSettings()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UiSettingsPath)!);
            var ap = Services.Appearance.Instance;
            File.WriteAllText(UiSettingsPath, JsonSerializer.Serialize(
                new UiSettings(ShowFramesPanel, ShowWavePanel, WaveFollow, TxColorHex, RxColorHex, BaudText,
                    TxCyclic, TxPeriodMs, ShowTerminalPanel,
                    SshHost, SshPort, SshUser, SshAuthIndex, SshKeyPath,
                    ap.MainBgHex, ap.TerminalBgHex, ap.ChartBgHex, ap.TemplateBgHex, ap.TermContentBgHex,
                    ap.PanelHex, ap.ButtonBgHex, ap.BorderHex, ap.TextHex, ap.MutedHex, ap.AccentHex,
                    ap.HoverHex, ap.SelectedHex, ap.UiFontFamily, ap.UiFontSize),
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // 保存失败不影响功能
            AppLog.Warn($"UI 设置保存失败：{UiSettingsPath}", ex);
        }
    }

    partial void OnShowFramesPanelChanged(bool value)
    {
        AppLog.Info($"多帧面板：{(value ? "显示" : "隐藏")}");
        SaveUiSettings();
    }

    /// <summary>勾选即按当前输入立即发首帧（连接未开/HEX 非法时提示并保留勾选，下周期自动重试）；
    /// 取消即停。开关与周期均持久化。</summary>
    partial void OnTxCyclicChanged(bool value)
    {
        AppLog.Info(value ? $"主发送区定时发送开启（周期 {Math.Max(TxPeriodMs, CyclicTickMs)} ms）" : "主发送区定时发送关闭");
        if (value)
        {
            _txNextDue = DateTime.Now.AddMilliseconds(Math.Max(TxPeriodMs, CyclicTickMs));
            if (!IsPortOpen)
                StatusText = "定时发送已开启：连接未打开，将在连接后按周期发送";
            else if (!SendMain(silent: false))
                StatusText = "定时发送已开启：当前内容为空或 HEX 非法，修正后下周期自动发送";
        }
        SaveUiSettings();
    }

    partial void OnTxPeriodMsChanged(int value) => SaveUiSettings();

    partial void OnShowWavePanelChanged(bool value)
    {
        AppLog.Info($"波形窗口：{(value ? "打开" : "关闭")}");
        SaveUiSettings();
    }

    partial void OnShowTerminalPanelChanged(bool value)
    {
        AppLog.Info($"终端窗口：{(value ? "打开" : "关闭")}");
        SaveUiSettings();
    }

    partial void OnWaveFollowChanged(bool value) => SaveUiSettings();

    partial void OnBaudTextChanged(string value) => SaveUiSettings();

    partial void OnSelectedBaudItemChanged(string? value)
    {
        if (value is null) return;
        if (value == CustomBaudLabel)
        {
            // 延迟到本次选择提交完全结束后再弹框：同步弹框（模态嵌套消息循环）会让
            // ComboBox 未走完的提交流程在关框后把选中项回写成「自定义…」，冲掉自定义值
            Application.Current?.Dispatcher.BeginInvoke(new Action(HandleCustomBaud));
            return;
        }
        if (value != BaudText) BaudText = value;
    }

    /// <summary>「自定义…」选项：弹输入对话框。确定 → 值插入下拉（「自定义…」之前）并选中；
    /// 取消 → 回退原选择（对话框嵌套在选择变更回调里，关闭后下拉已收起）。</summary>
    private void HandleCustomBaud()
    {
        var prev = BaudRates.Contains(BaudText) ? BaudText : null;
        var win = new CustomBaudWindow(SelectedBaud > 0 ? BaudText : "115200")
        {
            Owner = Application.Current?.MainWindow,
        };
        if (win.ShowDialog() == true)
        {
            EnsureCustomItem(win.Value);
            BaudText = win.Value;
            SelectedBaudItem = win.Value;
        }
        else
        {
            if (prev is null && SelectedBaud > 0)
            {
                EnsureCustomItem(BaudText);
                prev = BaudText;
            }
            SelectedBaudItem = prev;
        }
    }

    /// <summary>自定义值插入「自定义…」之前（已存在则不动）。</summary>
    private void EnsureCustomItem(string v)
    {
        if (!BaudRates.Contains(v)) BaudRates.Insert(BaudRates.Count - 1, v);
    }

    /// <summary>启动时把选中项对齐到持久化的 BaudText：自定义值先插列表再选中。</summary>
    private void SyncBaudSelection()
    {
        if (SelectedBaud <= 0) return;
        EnsureCustomItem(BaudText);
        if (SelectedBaudItem != BaudText) SelectedBaudItem = BaudText;
    }

    /// <summary>清空发送输入框（「发送区」清空按钮）。</summary>
    [RelayCommand]
    private void ClearTx() => TxInput = string.Empty;

    [RelayCommand]
    private void ClearRx()
    {
        AppLog.Info($"清空接收区（原 {_lines.Count} 行）");
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
        RxRendered?.Invoke(this, new RxRender(RxRenderKind.Clear, Array.Empty<RxSeg>()));
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
        AppLog.Info($"会话数据日志路径变更：{LogFilePath}");
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

    /// <summary>开启日志：打开文件并写会话头，随后倾倒接收区当前全部内容
    ///（用户规则：日志包含开启前已收到的数据，不只记接下来的；文本由 MainWindow 注入捕获）。</summary>
    private void StartLogging()
    {
        try
        {
            _logger.Open(LogFilePath);
            _logger.WriteLine($"===== Serial Tool 会话 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====");
            var existing = CaptureRxText?.Invoke();
            if (!string.IsNullOrEmpty(existing))
            {
                _logger.WriteLine("----- 开启前接收区内容 -----");
                _logger.Write(existing.Replace("\r\n", Environment.NewLine));
                if (!existing.EndsWith("\n")) _logger.WriteLine("");
                _logger.WriteLine($"----- 实时记录开始 {DateTime.Now:HH:mm:ss.fff} -----");
            }
            AppLog.Info($"会话数据日志开启：{LogFilePath}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"会话数据日志开启失败：{LogFilePath}", ex);
            LogEnabled = false;
            StatusText = $"日志开启失败: {ex.Message}";
        }
    }

    /// <summary>接收区当前全文捕获（MainWindow 注入；RichTextBox 文本，行尾 \r\n）。</summary>
    public Func<string>? CaptureRxText { get; set; }

    // ---------- 数据流 ----------

    /// <summary>原始 RX 字节旁路（读取线程抛出）：终端视图订阅（线程安全入队，UI 泵消费）。
    /// 在帧解析分支之前抛出——终端始终拿原始字节流，与接收区显示模式无关。</summary>
    public event EventHandler<byte[]>? RawRxTap;

    /// <summary>读取线程回调：仅入队/喂解析器/记录波形，不做任何 UI 操作。</summary>
    private void OnDataReceived(object? sender, TimedData e)
    {
        RawRxTap?.Invoke(this, e.Bytes);
        Interlocked.Add(ref _pendingRxBytes, e.Bytes.Length);
        AppendWave(_rxWave, e.Bytes, e.Timestamp, ref _rxPrev); // 波形与解析/显示模式无关
        if (_parser != null)
        {
            // 帧解析模式：原始字节流进解析器，接收区只显示解出的帧。
            // Feed 在读线程执行且无上层兜底——解析器内部任何未预见异常都会终结进程，
            // 这里兜底：拆掉解析器转为直通显示（宁可丢解析不能崩进程）。
            try
            {
                _parser.Feed(e.Bytes);
            }
            catch (Exception ex)
            {
                AppLog.Error("帧解析器异常（已自动关闭解析，转为原始显示）", ex);
                _parser.FrameEmitted -= OnFrameEmitted;
                _parser = null;
                _rxQueue.Enqueue(new RxItem(e.Timestamp, e.Bytes, IsTx: false));
            }
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

        var segs = new SegBuilder();
        foreach (var item in newItems)
        {
            var line = FormatLine(item);
            if (_logger.IsActive)
                _logger.WriteLine(line);
            if (PassFilter(item))
                segs.Add(line, item.IsTx);
        }

        RxRendered?.Invoke(this, new RxRender(RxRenderKind.Append, segs.ToList()));
        WaveRendered?.Invoke(this, EventArgs.Empty);
        FieldPlotsRendered?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>同方向行合并器：FlushRx / BuildFullSegs 共用，连续同方向行并成一段减少视图着色次数。</summary>
    private sealed class SegBuilder
    {
        private readonly List<RxSeg> _segs = new();
        private System.Text.StringBuilder? _cur;
        private bool _curTx;

        public void Add(string line, bool isTx)
        {
            if (_cur is null || _curTx != isTx)
            {
                Flush();
                _cur = new System.Text.StringBuilder(line.Length + 1);
                _curTx = isTx;
            }
            _cur.Append(line).Append('\n');
        }

        /// <summary>收尾输出（当前段入列）。</summary>
        public List<RxSeg> ToList()
        {
            Flush();
            return _segs;
        }

        private void Flush()
        {
            if (_cur is null) return;
            _segs.Add(new RxSeg(_cur.ToString(), _curTx));
            _cur = null;
        }
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
            AppLog.Info($"自动应答命中[{r.Name}]（第 {r.HitCount} 次）：待发 {bytes.Length} 字节，延迟 {Math.Max(0, r.DelayMs)} ms");
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

    /// <summary>全量分段（显示模式/过滤/颜色切换全量重绘用；应用当前过滤）。</summary>
    private IReadOnlyList<RxSeg> BuildFullSegs()
    {
        var segs = new SegBuilder();
        foreach (var item in _lines)
            if (PassFilter(item))
                segs.Add(FormatLine(item), item.IsTx);
        return segs.ToList();
    }

    private void OnBackendError(object? sender, string msg)
    {
        AppLog.Error($"后端错误中断（{(sender as IBusBackend)?.Name ?? "未知"}，{_activeDesc}）：{msg}");
        Dispatch(() =>
        {
            _active = null;
            IsPortOpen = false;
            OnLinkClosed();
            StatusText = msg;
        });
    }

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
            case nameof(ShowHex) or nameof(ShowTimestamp) or nameof(RxFilterText) or nameof(FilterEnabled)
                or nameof(TxColorHex) or nameof(RxColorHex):
                // 显示模式/过滤/颜色切换：全量重绘，视图恢复原滚动位置
                RxRendered?.Invoke(this, new RxRender(RxRenderKind.Full, BuildFullSegs()));
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
                    AppLog.Info("会话数据日志停止");
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
