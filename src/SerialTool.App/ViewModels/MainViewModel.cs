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
public sealed record RxItem(DateTime Ts, byte[] Bytes, bool IsTx, ParsedFrame? Frame = null);

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

/// <summary>主窗口视图模型：端口管理 + 收发控制台。</summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int MaxLines = 2000;      // 行缓冲上限（超出丢弃最旧行）
    private const int FlushIntervalMs = 50; // UI 批量刷新周期
    private const int CyclicTickMs = 50;    // 循环发送调度粒度（也是最小周期）
    private readonly DispatcherTimer _cyclicTimer;

    private readonly SerialBackend _serialBackend = new();
    private readonly TcpBackend _tcpBackend = new();

    /// <summary>当前活动连接（串口或 TCP），未连接为 null。</summary>
    private IBusBackend? _active;

    private readonly SessionLogger _logger = new();
    private readonly ConcurrentQueue<RxItem> _rxQueue = new();
    private readonly List<RxItem> _lines = new();
    private readonly DispatcherTimer _flushTimer;
    private long _pendingRxBytes;

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

    public bool IsPortClosed => !IsPortOpen;

    /// <summary>接收框渲染事件：视图订阅后直接操作 TextBox（追加保留滚动位置）。</summary>
    public event EventHandler<RxRender>? RxRendered;

    public IReadOnlyList<int> BaudRates { get; } = new[]
        { 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600 };

    public IReadOnlyList<int> DataBitsOptions { get; } = new[] { 8, 7 };
    public IReadOnlyList<string> StopBitsOptions { get; } = new[] { "1", "1.5", "2" };
    public IReadOnlyList<string> ParityOptions { get; } = new[] { "无", "偶", "奇" };

    /// <summary>多帧发送列表（持久化到 Config/send_frames.json）。</summary>
    public ObservableCollection<SendFrameViewModel> SendFrames { get; } = new();

    [ObservableProperty]
    private SendFrameViewModel? _selectedFrame;

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

        // 预创建日志目录：保证"打开目录"按钮始终有目录可开
        try { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(LogFilePath)!); }
        catch { /* 目录创建失败时打开按钮会提示 */ }

        LoadUiSettings();
        LoadTemplates();
        _ = LoadPortsAsync();
        LoadFrames();
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

    /// <summary>解析线程回调：帧入队（帧模式下原始字节流不再逐块显示）。</summary>
    private void OnFrameEmitted(ParsedFrame frame)
        => _rxQueue.Enqueue(new RxItem(frame.Ts, frame.Raw, IsTx: false, frame));

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
        TogglePortCommand.NotifyCanExecuteChanged();
        // 切换连接方式时若已连接则先断开
        if (IsPortOpen)
        {
            _active?.Close();
            _active = null;
            IsPortOpen = false;
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
        }
        catch (Exception ex)
        {
            StatusText = $"连接失败: {ex.Message}";
        }
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

    /// <summary>写入当前活动连接并回显；silent=true 时不刷状态栏（循环发送防噪音）。</summary>
    private void WriteBytes(byte[] bytes, string? label = null, bool silent = false)
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
        _rxQueue.Enqueue(new RxItem(DateTime.Now, bytes, IsTx: true));
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

    // ---------- UI 设置持久化 ----------

    private sealed record UiSettings(bool ShowFramesPanel);

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
                    ShowFramesPanel = s.ShowFramesPanel;
            }
        }
        catch
        {
            // 设置损坏时使用默认值
        }
    }

    partial void OnShowFramesPanelChanged(bool value)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(UiSettingsPath)!);
            File.WriteAllText(UiSettingsPath,
                JsonSerializer.Serialize(new UiSettings(value), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败不影响功能
        }
    }

    [RelayCommand]
    private void ClearRx()
    {
        _lines.Clear();
        RxCount = 0;
        TxCount = 0;
        FrameOkCount = 0;
        FrameErrCount = 0;
        _parser?.Reset();
        RxRendered?.Invoke(this, new RxRender(RxRenderKind.Clear, string.Empty));
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

    /// <summary>读取线程回调：仅入队/喂解析器，不做任何 UI 操作。</summary>
    private void OnDataReceived(object? sender, TimedData e)
    {
        Interlocked.Add(ref _pendingRxBytes, e.Bytes.Length);
        if (_parser != null)
        {
            // 帧解析模式：原始字节流进解析器，接收区只显示解出的帧
            _parser.Feed(e.Bytes);
            return;
        }
        _rxQueue.Enqueue(new RxItem(e.Timestamp, e.Bytes, IsTx: false));
    }

    /// <summary>UI 定时批量投递：高波特率下避免逐字节刷新；同时把新行落盘。
    /// 渲染走追加事件（不整体重置文本，保留用户滚动位置）。</summary>
    private void FlushRx(object? sender, EventArgs e)
    {
        if (_rxQueue.IsEmpty) return;

        var newItems = new List<RxItem>();
        while (_rxQueue.TryDequeue(out var item))
        {
            _lines.Add(item);
            newItems.Add(item);
        }
        while (_lines.Count > MaxLines)
            _lines.RemoveAt(0);

        RxCount += Interlocked.Exchange(ref _pendingRxBytes, 0);

        // 帧统计
        long ok = 0, err = 0;
        foreach (var item in newItems)
        {
            if (item.Frame is null) continue;
            if (item.Frame.Ok) ok++; else err++;
        }
        if (ok > 0) FrameOkCount += ok;
        if (err > 0) FrameErrCount += err;

        var sb = new System.Text.StringBuilder(newItems.Count * 32);
        foreach (var item in newItems)
        {
            var line = FormatLine(item);
            if (_logger.IsActive)
                _logger.WriteLine(line);
            sb.Append(line).Append('\n');
        }
        RxRendered?.Invoke(this, new RxRender(RxRenderKind.Append, sb.ToString()));
    }

    /// <summary>单行格式化：[时间戳] 方向 数据（显示与日志共用）。</summary>
    private string FormatLine(RxItem item)
    {
        if (item.Frame is { } f)
            return FormatFrameLine(item.Ts, f);

        var sb = new System.Text.StringBuilder(48 + item.Bytes.Length * 3);
        if (ShowTimestamp)
            sb.Append('[').Append(item.Ts.ToString("HH:mm:ss.fff")).Append("] ");
        sb.Append(item.IsTx ? "→ " : "← ");
        sb.Append(ShowHex ? Hex.Encode(item.Bytes) : Hex.ToAscii(item.Bytes));
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

    /// <summary>全量文本（显示模式切换时全量重绘用）。</summary>
    private string BuildFullText()
    {
        var sb = new System.Text.StringBuilder(_lines.Count * 32);
        foreach (var item in _lines)
            sb.Append(FormatLine(item)).Append('\n');
        return sb.ToString();
    }

    private void OnBackendError(object? sender, string msg)
        => Dispatch(() =>
        {
            _active = null;
            IsPortOpen = false;
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
            case nameof(ShowHex) or nameof(ShowTimestamp):
                // 显示模式切换：全量重绘，视图恢复原滚动位置
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
