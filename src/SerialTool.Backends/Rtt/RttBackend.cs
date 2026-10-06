namespace SerialTool.Backends.Rtt;

/// <summary>RTT 连接参数。Interface：0=SWD，1=JTAG；SerialNumber=null 取默认 USB 探针；
/// ResetTarget=连接后复位目标并运行（RTT-T 同款默认，防目标 halt 固件不跑）；
/// ControlBlockAddress=null 自动搜索 RAM（大 RAM 芯片首连可达数秒）。</summary>
public sealed record RttConfig(string Device, int SpeedKhz = 4000, int Interface = 0,
    int Channel = 0, uint? ControlBlockAddress = null, uint? SerialNumber = null, bool ResetTarget = true);

/// <summary>SEGGER RTT 后端接口。</summary>
public interface IRttBackend : IBusBackend
{
    /// <summary>连接 J-Link 探针并启动 RTT。阻塞可达数秒（探针连接 + 控制块搜索），必须后台线程调用。
    /// 失败抛异常（消息可直接展示）。同一进程同时只支持一个 J-Link 连接。</summary>
    void Open(RttConfig cfg);
}

/// <summary>
/// SEGGER RTT 后端：经 J-Link 探针读写目标机 RAM 中的 RTT 环形缓冲（需固件已集成 SEGGER RTT）。
/// 与串口/SSH 后端同构的事件流，终端视图等上层功能复用。
/// 连接序列对齐 pylink-square（RTT-T 的底层库）：选探针→OpenEx→TIF→速度→Device（触发自动连接）
/// →未连则 Connect→复位+运行→RTT START。
/// J-Link DLL 由 <see cref="JLinkNative"/> 进程级常驻；本后端实例只持连接，Close 即 JLINKARM_Close。
/// 注意：J-Link DLL 同进程默认单连接——同时只允许一个 RttBackend 处于打开状态。
/// </summary>
public sealed class RttBackend : IRttBackend
{
    // 进程级单连接槽：DLL 对二次 Connect 的行为是静默改连目标而非报错，必须前置拦截。
    private static readonly object ActiveLock = new();
    private static RttBackend? _active;

    private JLinkNative? _jlink;
    private Thread? _readThread;
    private volatile bool _running;
    private int _closed; // Interlocked 幂等位：UI 关标签与读线程自关闭可能并发调 Close
    private int _channel;

    private const int ReadBufSize = 4096;
    private const int IdlePollMs = 10;        // 空闲轮询：RTT 目标通常毫秒级打印，10ms 聚合够低延迟也不空转
    private const int WriteTimeoutMs = 2000;  // 目标持续不读下行缓冲的判定窗口
    private const int WriteRetrySleepMs = 10;
    private const int JoinTimeoutMs = 1500;

    /// <summary>J-Link 原生层消息（被抑制的 DLL 弹窗文本等）。订阅方只许做字符串记录（如转运行日志）。</summary>
    public static event Action<string>? JLinkLog;

    static RttBackend()
    {
        // 静态构造早于任何 EnsureLoaded：hook 抑制的弹窗消息从此不再丢失
        JLinkNative.Log += msg => JLinkLog?.Invoke(msg);
    }

    public string Name => "RTT";
    public bool IsOpen => _running;

    public event EventHandler<TimedData>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>枚举 USB 上的 J-Link 探针。未装 J-Link 软件/无探针返回空列表（不抛）。
    /// 单探针时 UI 会把 S/N 预填进连接框；多探针按 S/N 选择。</summary>
    public IReadOnlyList<DeviceInfo> Scan()
    {
        if (!JLinkNative.TryLoad(out var jlink, out _))
            return Array.Empty<DeviceInfo>();
        var sns = new uint[32];
        int count;
        lock (jlink!.Gate)
            count = jlink.EmuGetListUsb(sns);
        if (count <= 0)
            return Array.Empty<DeviceInfo>();
        return Enumerable.Range(0, Math.Min(count, sns.Length))
            .Select(i => new DeviceInfo(sns[i].ToString(), $"J-Link S/N {sns[i]}"))
            .ToList();
    }

    public void Open(RttConfig cfg)
    {
        Validate(cfg);
        Close(); // 复用实例重开：复位幂等位与残留状态

        lock (ActiveLock)
        {
            if (_active is { IsOpen: true })
                throw new InvalidOperationException("同一进程同时只支持一个 J-Link 连接：请先关闭现有 RTT 会话");
            _active = this;
        }

        var jlink = JLinkNative.EnsureLoaded(); // 未装 J-Link 软件 → DllNotFoundException（含安装指引）
        bool dllOpened = false;
        try
        {
            lock (jlink.Gate)
            {
                // 序列对齐 pylink（RTT-T 底层库）——顺序有讲究：
                // 'Device =' 命令会触发自动连接，必须先设好接口与速度，否则用默认接口连错目标
                jlink.OpenProbe(cfg.SerialNumber); // 选探针（S/N 或默认 USB0）+ JLINKARM_OpenEx（日志回调接 JLinkLog）
                dllOpened = true;

                if (!jlink.TifSelect(cfg.Interface == 1))
                    throw new InvalidOperationException($"设置调试接口失败（{(cfg.Interface == 1 ? "JTAG" : "SWD")} 不受探针/目标支持）");
                jlink.SetSpeed(cfg.SpeedKhz);

                if (jlink.DeviceSupported(cfg.Device) == false)
                    throw new InvalidOperationException($"未知或不受支持的器件名 {cfg.Device}（须与目标芯片一致，如 STM32F103C8 / nRF52840_xxAA）");
                Exec(jlink, $"Device = {cfg.Device}", $"器件名 {cfg.Device} 设置失败");

                if (!jlink.IsConnected())
                {
                    // ExecCommand 错误常延迟到这里才暴露（弱语义）：器件名/接口错多报 -261 找不到 CPU
                    var ret = jlink.Connect();
                    if (ret < 0)
                        throw new InvalidOperationException(
                            $"J-Link 连接目标失败（{Describe(ret)}）：请检查器件名/接口/接线/目标供电");
                }

                // 复位+运行（RTT-T「每次连接复位 MCU」）：连接调试常令目标 halt，不跑固件 RTT 控制块就无人初始化
                if (cfg.ResetTarget)
                    jlink.ResetAndRun();

                var ret2 = StartRtt(jlink, cfg);
                if (ret2 < 0)
                {
                    var msg = ret2 == -2
                        ? "RTT 控制块未找到：确认固件已初始化 SEGGER RTT 且控制块未被链接器裁剪（--gc-sections/LTO），或在连接时手动指定控制块地址"
                        : $"启动 RTT 失败（{Describe(ret2)}）";
                    try { jlink.RttStop(); } catch { /* 清理路径失败忽略 */ }
                    throw new InvalidOperationException(msg);
                }
            }
        }
        catch
        {
            if (dllOpened)
                jlink.CloseDll();
            ReleaseSlot();
            throw;
        }

        _jlink = jlink;
        _channel = cfg.Channel;
        _closed = 0;
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"RttRead:{cfg.Device}",
        };
        _readThread.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var jlink = _jlink;
        if (!_running || jlink is null)
            throw new InvalidOperationException("RTT 未连接");
        var arr = data.ToArray();
        var deadline = Environment.TickCount64 + WriteTimeoutMs;
        var sent = 0;
        while (sent < arr.Length)
        {
            int n;
            lock (jlink.Gate)
            {
                if (!_running)
                    throw new IOException("RTT 已断开");
                unsafe { fixed (byte* p = &arr[sent]) n = jlink.RttWrite(_channel, p, arr.Length - sent); }
            }
            if (n < 0)
                throw new IOException($"RTT 写入失败（{Describe(n)}）");
            if (n > 0)
            {
                sent += n;
                continue; // 部分写：推进后立即续写
            }
            // n == 0：目标机下行缓冲满（目标不读输入是常态故障而非偶发）——锁外退避重试
            if (Environment.TickCount64 > deadline)
                throw new IOException("RTT 写入超时：目标机持续未读取下行缓冲（固件未消费 RTT 输入？）");
            Thread.Sleep(WriteRetrySleepMs);
        }
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 1)
            return; // 幂等：UI 关标签 / 读线程自关闭 / Dispose 可能并发
        _running = false;
        var t = _readThread;
        if (t is not null && !ReferenceEquals(t, Thread.CurrentThread))
            t.Join(JoinTimeoutMs); // 读线程自己调 Close（探针拔出自恢复）时跳过 Join
        lock (ActiveLock)
        {
            if (ReferenceEquals(_active, this))
                _active = null;
        }
        var jlink = _jlink;
        if (jlink is not null)
        {
            // Join 超时兜底：极端 USB 卡死时 Teardown 在 Gate 上排队等读返回（正常毫秒级）。
            // 不引入 TryEnter 放弃路径——跳过 JLINK_Close 会泄漏探针占用，比短暂阻塞更糟。
            lock (jlink.Gate)
            {
                try { jlink.RttStop(); } catch { }
                jlink.CloseDll();
            }
        }
        _jlink = null;
        _readThread = null;
    }

    public void Dispose() => Close();

    private void ReadLoop()
    {
        var jlink = _jlink!;
        var buf = new byte[ReadBufSize];
        while (_running)
        {
            int n;
            lock (jlink.Gate)
            {
                if (!_running)
                    break; // 锁内二次检查：Close 抢到锁关 DLL 后不再触碰
                unsafe { fixed (byte* p = buf) n = jlink.RttRead(_channel, p, buf.Length); }
            }
            if (n > 0)
            {
                var data = new byte[n];
                Array.Copy(buf, data, n);
                DataReceived?.Invoke(this, new TimedData(DateTime.Now, data));
                continue; // 有数据立即续读排空（单批上限 4KB）
            }
            if (n < 0)
            {
                // 探针拔出/目标断电：报中断并自关闭（Close 的 ReferenceEquals 分支跳过 Join）
                if (_running)
                    ErrorOccurred?.Invoke(this, $"J-Link 连接中断（{Describe(n)}）");
                Close();
                return;
            }
            Thread.Sleep(IdlePollMs);
        }
    }

    private static void Exec(JLinkNative jlink, string cmd, string errPrefix)
    {
        var ret = jlink.ExecCommand(cmd);
        if (ret < 0)
            throw new InvalidOperationException($"{errPrefix}（{Describe(ret)}）");
    }

    /// <summary>RTT START；复位过目标时 -2（控制块未找到）补一次重试——复位后固件初始化 RTT 需要片刻，
    /// 立即搜索可能扑空（pylink/RTT-T 无此场景：它们复位后同样立刻 start，靠 DLL 后台续搜；这里显式重试一次更稳）。</summary>
    private static int StartRtt(JLinkNative jlink, RttConfig cfg)
    {
        var ret = jlink.RttStart(cfg.ControlBlockAddress);
        if (ret == -2 && cfg.ResetTarget && cfg.ControlBlockAddress is null)
        {
            Thread.Sleep(300);
            ret = jlink.RttStart(cfg.ControlBlockAddress);
        }
        return ret;
    }

    private void ReleaseSlot()
    {
        lock (ActiveLock)
        {
            if (ReferenceEquals(_active, this))
                _active = null;
        }
    }

    private static void Validate(RttConfig cfg)
    {
        if (string.IsNullOrWhiteSpace(cfg.Device) || !cfg.Device.All(c => c < 128))
            throw new InvalidOperationException("器件名不能为空且须为 ASCII（如 STM32F407VG）");
        if (cfg.SpeedKhz is < 1000 or > 50000)
            throw new InvalidOperationException("接口速度须在 1000–50000 kHz 之间");
        if (cfg.Interface is not (0 or 1))
            throw new InvalidOperationException("接口须为 SWD(0) 或 JTAG(1)");
        if (cfg.Channel is < 0 or > 3)
            throw new InvalidOperationException("RTT 通道须在 0–3 之间");
    }

    /// <summary>J-Link 错误码 → 中文描述（pylink 官方错误码表）。</summary>
    internal static string Describe(int code) => code switch
    {
        -2 => "RTT 控制块未找到（-2）",
        -256 => "探针无连接（-256）",
        -258 => "DLL 未打开（-258）",
        -259 => "目标 VCC 检测失败（-259）",
        -261 => "找不到 CPU：器件名/接口/接线/供电错误（-261）",
        -1 => "通用错误（-1）",
        _ => $"J-Link 错误码 {code}",
    };
}
