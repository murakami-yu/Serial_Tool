using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SerialTool.Backends.Rtt;

/// <summary>
/// J-Link 原生 DLL（JLink_x64.dll / JLinkARM.dll）封装：进程级单例，加载后常驻不卸载。
/// J-Link DLL 内部有工作线程/TLS/全局状态，FreeLibrary 时机稍错即残留线程跳进已释放代码而崩溃
/// （JLinkExe / Ozone / pylink 均进程常驻）；断开连接只走 JLINKARM_Close（连接级复位），不动库。
/// DLL 不随应用分发（SEGGER 许可要求），运行时定位顺序：程序目录 → 注册表 → 环境变量 → Program Files。
/// 所有 J-Link API 非线程安全：跨线程调用必须持 <see cref="Gate"/> 串行。
/// API 序列对齐 pylink-square（RTT-T 等工具的底层库，真机验证充分）。
/// </summary>
internal sealed class JLinkNative
{
    private const string DllName = "JLink_x64.dll";
    private const string DllNameAlt = "JLinkARM.dll";

    // RTT Control 命令码（JLinkRTTCommand：START=0/STOP=1，pylink 官方枚举）
    private const int CmdStart = 0;
    private const int CmdStop = 1;

    // JLinkHost.USB
    private const int HostIfUsb = 1;

    // JLINKARM_TIF_Select 接口号（pylink JLinkInterfaces：JTAG=0 / SWD=1——与本应用配置编码相反）
    private const int TifJtag = 0;
    private const int TifSwd = 1;

    private static JLinkNative? _instance;
    private static readonly object InstanceLock = new();

    private readonly IntPtr _handle;
    private readonly DGetDllVersion _getDllVersion;
    private readonly DExecCommand _execCommand;
    private readonly DSelectUsb _selectUsb;
    private readonly DSelectByUsbSn _selectByUsbSn;
    private readonly DOpenEx _openEx;
    private readonly DClose _close;
    private readonly DTifSelect _tifSelect;
    private readonly DSetSpeed _setSpeed;
    private readonly DConnect _connect;
    private readonly DIsConnected _isConnected;
    private readonly DRttControl _rttControl;
    private readonly DRttRead _rttRead;
    private readonly DRttWrite _rttWrite;
    private readonly DEmuGetList _emuGetList;

    // 可选导出（旧版 DLL 可能没有）：缺失则降级跳过对应特性，不阻断加载
    private readonly DSetHookUnsecureDialog? _setHook;
    private readonly DDeviceGetIndex? _deviceGetIndex;
    private readonly DSetResetDelay? _setResetDelay;
    private readonly DReset? _reset;
    private readonly DGo? _go;

    /// <summary>DLL 消息（OpenEx 日志/错误回调 + 被抑制的弹窗文本，多来自 DLL 内部线程）。
    /// 订阅方只许做字符串记录：回调上下文不可控（可能持 DLL 内部锁），不做任何锁/UI 操作。</summary>
    public static event Action<string>? Log;

    /// <summary>全 DLL 串行锁：J-Link API 非线程安全，所有调用（含 Scan 的探针枚举）共用一把。</summary>
    public object Gate { get; } = new();

    /// <summary>实际加载的 DLL 全路径（日志/诊断用）。</summary>
    public string DllPath { get; }

    /// <summary>原始版本号（如 80208 → v8.02h）。</summary>
    public int Version { get; }

    private JLinkNative(string dllPath)
    {
        DllPath = dllPath;
        _handle = NativeLibrary.Load(dllPath);
        try
        {
            _getDllVersion = Bind<DGetDllVersion>("JLINKARM_GetDLLVersion");
            _execCommand = Bind<DExecCommand>("JLINKARM_ExecCommand");
            _selectUsb = Bind<DSelectUsb>("JLINKARM_SelectUSB");
            _selectByUsbSn = Bind<DSelectByUsbSn>("JLINKARM_EMU_SelectByUSBSN");
            _openEx = Bind<DOpenEx>("JLINKARM_OpenEx");
            _close = Bind<DClose>("JLINKARM_Close");
            _tifSelect = Bind<DTifSelect>("JLINKARM_TIF_Select");
            _setSpeed = Bind<DSetSpeed>("JLINKARM_SetSpeed");
            _connect = Bind<DConnect>("JLINKARM_Connect");
            _isConnected = Bind<DIsConnected>("JLINKARM_IsConnected");
            _rttControl = Bind<DRttControl>("JLINK_RTTERMINAL_Control");
            _rttRead = Bind<DRttRead>("JLINK_RTTERMINAL_Read");
            _rttWrite = Bind<DRttWrite>("JLINK_RTTERMINAL_Write");
            _emuGetList = Bind<DEmuGetList>("JLINKARM_EMU_GetList");
        }
        catch
        {
            NativeLibrary.Free(_handle); // 初始化期回滚（运行期常驻不 Free 是另一回事）
            throw;
        }

        // 可选导出：拿不到不算致命（旧 DLL 降级：无弹窗 hook / 无器件名校验 / 无目标复位）
        _setHook = TryBind<DSetHookUnsecureDialog>("JLINKARM_SetHookUnsecureDialog");
        _deviceGetIndex = TryBind<DDeviceGetIndex>("JLINKARM_DEVICE_GetIndex");
        _setResetDelay = TryBind<DSetResetDelay>("JLINKARM_SetResetDelay");
        _reset = TryBind<DReset>("JLINKARM_Reset");
        _go = TryBind<DGo>("JLINKARM_Go");

        Version = _getDllVersion();

        // 弹窗抑制：装上回调后 DLL 出错走回调而非自己的 MessageBox（否则连接失败会弹原生对话框卡线程）。
        // 必须早于任何探针打开。
        _setHook?.Invoke(HookCallback);
        ExecCommand("HideDeviceSelectionDialog = 1"); // 兜底：hook 未必覆盖所有弹框类型
    }

    /// <summary>静态回调根持（委托被 GC 回收后再被 native 调用 = 崩溃）。
    /// 方法组到含指针签名委托的转换须 unsafe 上下文，经工厂方法桥接；static 保证不捕获 this。</summary>
    private static readonly DLogCallback HookCallback = CreateHook();
    private static readonly DLogCallback DllLogCallback = CreateLogCb();
    private static readonly DLogCallback DllErrCallback = CreateErrCb();

    private static unsafe DLogCallback CreateHook() => OnUnsecureDialog;
    private static unsafe DLogCallback CreateLogCb() => OnDllLog;
    private static unsafe DLogCallback CreateErrCb() => OnDllError;

    /// <summary>以下回调只做字符串转发，try/catch 全包：异常穿越 native 边界 = 进程崩溃，取锁 = 潜在死锁。</summary>
    private static unsafe void OnUnsecureDialog(byte* msgPtr) => Forward(msgPtr, "DLL: ");

    private static unsafe void OnDllLog(byte* msgPtr) => Forward(msgPtr, "DLL: ");

    private static unsafe void OnDllError(byte* msgPtr) => Forward(msgPtr, "DLL错误: ");

    private static unsafe void Forward(byte* msgPtr, string prefix)
    {
        try
        {
            var msg = msgPtr != null ? Marshal.PtrToStringAnsi((IntPtr)msgPtr) : null;
            if (!string.IsNullOrEmpty(msg))
                Log?.Invoke(prefix + msg);
        }
        catch
        {
            // 绝不允许异常穿越 native 边界
        }
    }

    /// <summary>定位并加载 DLL（已加载直接返回）。未安装时抛 <see cref="DllNotFoundException"/>（消息可直接展示给用户）。</summary>
    public static JLinkNative EnsureLoaded()
    {
        var inst = _instance;
        if (inst is not null) return inst;
        lock (InstanceLock)
        {
            if (_instance is not null) return _instance;
            var path = LocateDll()
                ?? throw new DllNotFoundException(
                    "未找到 SEGGER J-Link 软件（JLink_x64.dll）。已尝试：本程序目录（JLink_x64.dll / JLinkARM.dll）、" +
                    "注册表 HKLM/HKCU\\SOFTWARE\\SEGGER\\J-Link、环境变量 SEGGER_JLINK_ROOT_PATH、Program Files\\SEGGER\\JLink*。" +
                    "解决办法：把 JLink_x64.dll 直接复制到本程序目录（与 RTT-T 工具同模式），或从 SEGGER 官网安装免费的 J-Link 软件后重试。");
            _instance = new JLinkNative(path);
            return _instance;
        }
    }

    /// <summary>温和版加载（供 Scan 等探测场景：未安装不抛，返回 false 带错误描述）。</summary>
    public static bool TryLoad(out JLinkNative? jlink, out string error)
    {
        try
        {
            jlink = EnsureLoaded();
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            jlink = null;
            error = ex.Message;
            return false;
        }
    }

    /// <summary>版本号格式化为 SEGGER 惯例（80208 → "v8.02h"）。</summary>
    public string VersionString
    {
        get
        {
            var major = Version / 10000;
            var minor = (Version % 10000) / 100;
            var patch = Version % 100;
            var letter = patch >= 1 && patch <= 26 ? ((char)('a' + patch - 1)).ToString() : $".{patch}";
            return $"v{major}.{minor:00}{letter}";
        }
    }

    /// <summary>执行 J-Link 命令（如 "Device = STM32F407VG"）。ASCII 编码 + NUL 结尾——
    /// GetDelegateForFunctionPointer 路径没有自动 charset 转换，必须手动编码。&lt;0 为错误码。</summary>
    public int ExecCommand(string cmd)
    {
        var bytes = new byte[Encoding.ASCII.GetByteCount(cmd) + 1];
        Encoding.ASCII.GetBytes(cmd, bytes);
        unsafe { fixed (byte* p = bytes) return _execCommand(p, 0, 0); }
    }

    /// <summary>打开探针（pylink open() 同款）：先选探针（S/N 或默认 USB0），再 JLINKARM_OpenEx。
    /// OpenEx 的日志/错误回调接进 <see cref="Log"/>——DLL 全程输出可见，现场排障不再两眼一抹黑。
    /// 失败抛异常（消息含 DLL 给出的原因，如另一进程占用探针）。</summary>
    public void OpenProbe(uint? serialNumber)
    {
        if (serialNumber is { } sn)
        {
            if (_selectByUsbSn(sn) < 0)
                throw new InvalidOperationException($"未找到 S/N {sn} 的 J-Link 探针（未连接或已被其他程序占用）");
        }
        else if (_selectUsb(0) != 0)
        {
            throw new InvalidOperationException("未找到 USB 上的 J-Link 探针（请检查探针连接）");
        }

        IntPtr err;
        unsafe { err = _openEx(DllLogCallback, DllErrCallback); }
        if (err != IntPtr.Zero)
        {
            var msg = Marshal.PtrToStringAnsi(err);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(msg)
                ? "打开 J-Link 探针失败"
                : $"打开 J-Link 探针失败：{msg}（探针可能被 J-Link RTT Viewer 等其他程序占用）");
        }
    }

    /// <summary>选择目标接口（JLINKARM_TIF_Select）。DLL 编码 JTAG=0/SWD=1；返回 false = 不支持。</summary>
    public bool TifSelect(bool jtag) => _tifSelect(jtag ? TifJtag : TifSwd) == 0;

    /// <summary>设置接口速度 kHz（JLINKARM_SetSpeed）。</summary>
    public void SetSpeed(int khz) => _setSpeed(khz);

    /// <summary>目标是否已连上（JLINKARM_IsConnected）。</summary>
    public bool IsConnected() => _isConnected() != 0;

    /// <summary>器件名是否在 DLL 器件库中（JLINKARM_DEVICE_GetIndex，可选导出）。null = 无法校验（旧 DLL）。</summary>
    public bool? DeviceSupported(string device)
    {
        if (_deviceGetIndex is null) return null;
        var bytes = new byte[Encoding.ASCII.GetByteCount(device) + 1];
        Encoding.ASCII.GetBytes(device, bytes);
        unsafe
        {
            fixed (byte* p = bytes) return _deviceGetIndex(p) > 0;
        }
    }

    /// <summary>连接目标 CPU（JLINKARM_Connect；'Device =' 命令已触发自动连接时跳过）。&lt;0 为错误码。</summary>
    public int Connect() => _connect();

    /// <summary>复位目标并继续运行（pylink reset(ms, halt=False) 同款，RTT-T「每次连接复位 MCU」）：
    /// 连接调试常令目标 halt，不复位+运行固件就不跑、RTT 控制块无人初始化——这是 RTT 连上没数据的头号原因。
    /// 复位相关导出缺失（旧 DLL）时跳过并留日志。失败抛异常。</summary>
    public void ResetAndRun(int delayMs = 10)
    {
        if (_setResetDelay is null || _reset is null || _go is null)
        {
            Log?.Invoke("DLL 缺少复位导出（JLINKARM_SetResetDelay/Reset/Go），跳过目标复位");
            return;
        }
        _setResetDelay(delayMs);
        var ret = _reset();
        if (ret < 0)
            throw new InvalidOperationException($"目标复位失败（{RttBackend.Describe(ret)}）");
        _go();
    }

    /// <summary>关闭连接（JLINKARM_Close：连接级复位，DLL 常驻）。返回值忽略。</summary>
    public void CloseDll()
    {
        try { _close(); } catch { /* 关闭异常不影响状态复位 */ }
    }

    /// <summary>启动 RTT（JLINK_RTTERMINAL_Control START）。
    /// configBlockAddress=null 传 NULL（pylink 同款，自动搜索 RAM）；指定地址则传 16 字节结构。
    /// 0 成功，&lt;0 错误码（-2 = 控制块未找到）。</summary>
    public unsafe int RttStart(uint? configBlockAddress)
    {
        if (configBlockAddress is null)
            return _rttControl(CmdStart, null);
        // JLINK_RTTERMINAL_START_t = { u32 ConfigBlockAddress; u32 保留×3 }（16 字节，无 padding）
        uint* cfg = stackalloc uint[4];
        cfg[0] = configBlockAddress.Value;
        cfg[1] = 0;
        cfg[2] = 0;
        cfg[3] = 0;
        return _rttControl(CmdStart, cfg);
    }

    /// <summary>停止 RTT（STOP）。断开路径调用，失败忽略——错误只能经日志上浮。</summary>
    public unsafe int RttStop() => _rttControl(CmdStop, null);

    /// <summary>读 RTT 上行通道。返回实读字节数，&lt;0 错误码。</summary>
    public unsafe int RttRead(int bufferIndex, byte* buf, int size) => _rttRead(bufferIndex, buf, size);

    /// <summary>写 RTT 下行通道。返回实写字节数（部分写是常态：目标不读则下行缓冲满返回 0），&lt;0 错误码。</summary>
    public unsafe int RttWrite(int bufferIndex, byte* buf, int len) => _rttWrite(bufferIndex, buf, len);

    /// <summary>枚举 USB 探针 S/N（JLINKARM_EMU_GetList，HostIfs=USB）。返回探针数，&lt;=0 视为无。</summary>
    public unsafe int EmuGetListUsb(Span<uint> sns)
    {
        fixed (uint* p = sns)
            return _emuGetList(HostIfUsb, p, sns.Length);
    }

    private T Bind<T>(string exportName) where T : Delegate
    {
        var ptr = NativeLibrary.GetExport(_handle, exportName);
        return (T)Marshal.GetDelegateForFunctionPointer(ptr, typeof(T));
    }

    private T? TryBind<T>(string exportName) where T : Delegate
    {
        try { return Bind<T>(exportName); }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>DLL 定位链（优先级即顺序）：
    /// 程序目录（RTT-T 自带 DLL 同模式，用户可自行放置）→ 注册表 HKLM/HKCU（含 32 位视图）→
    /// 环境变量 SEGGER_JLINK_ROOT_PATH → Program Files / Program Files (x86) / 本地用户 Programs 下 SEGGER\JLink*。</summary>
    private static string? LocateDll()
    {
        // 0. 程序目录：把 DLL 放在 exe 旁边即生效（无需安装 J-Link 软件）
        foreach (var name in new[] { DllName, DllNameAlt })
        {
            var p = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(p)) return p;
        }

        // 1. 注册表 InstallPath（64 位装 HKLM\SOFTWARE\SEGGER\J-Link；老版本/32 位装 WOW6432Node 或 HKCU）
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Default, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\SEGGER\J-Link");
                if (key?.GetValue("InstallPath") is string dir)
                {
                    var p = Path.Combine(dir, DllName);
                    if (File.Exists(p)) return p;
                    p = Path.Combine(dir, DllNameAlt);
                    if (File.Exists(p)) return p;
                }
            }
            catch
            {
                // 注册表不可读走下一条
            }
        }

        // 2. 环境变量
        var env = Environment.GetEnvironmentVariable("SEGGER_JLINK_ROOT_PATH");
        if (!string.IsNullOrEmpty(env))
        {
            var p = Path.Combine(env, DllName);
            if (File.Exists(p)) return p;
            p = Path.Combine(env, DllNameAlt);
            if (File.Exists(p)) return p;
        }

        // 3. 常见安装目录通配（JLink、JLink_V794x 等目录名，倒序取最新版本）
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };
        foreach (var root in roots)
        {
            var segger = Path.Combine(root, "SEGGER");
            if (!Directory.Exists(segger)) continue;
            var hit = Directory.GetDirectories(segger, "JLink*")
                .SelectMany(d => new[] { Path.Combine(d, DllName), Path.Combine(d, DllNameAlt) })
                .FirstOrDefault(File.Exists);
            if (hit is not null) return hit;
        }
        return null;
    }

    // ---------- 委托声明（cdecl；x64 单一调用约定，与 stdcall 声明无差别） ----------
    // 全部存实例字段、实例被静态 _instance 根持：委托被 GC 回收后再被 native 调用 = 崩溃。

    private delegate int DGetDllVersion();
    private unsafe delegate int DExecCommand(byte* cmd, int a, int b);
    private delegate int DSelectUsb(int port);
    private delegate int DSelectByUsbSn(uint sn);
    private unsafe delegate IntPtr DOpenEx(DLogCallback log, DLogCallback err); // char* 错误串，NULL=成功
    private delegate void DClose();
    private delegate int DTifSelect(int tif);
    private delegate void DSetSpeed(int khz);
    private delegate int DConnect();
    private delegate int DIsConnected();
    private unsafe delegate int DDeviceGetIndex(byte* name);
    private delegate void DSetResetDelay(int ms);
    private delegate int DReset();
    private delegate void DGo();
    private unsafe delegate int DRttControl(int cmd, void* cmdData);
    private unsafe delegate int DRttRead(int bufferIndex, byte* buf, int size);
    private unsafe delegate int DRttWrite(int bufferIndex, byte* buf, int len);
    private unsafe delegate int DEmuGetList(int hostIfs, uint* sns, int maxItems);
    private unsafe delegate void DLogCallback(byte* msg);
    private unsafe delegate void DSetHookUnsecureDialog(DLogCallback hook);
}
