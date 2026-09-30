using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace SerialTool.Backends.Rtt;

/// <summary>
/// J-Link 原生 DLL（JLink_x64.dll）封装：进程级单例，加载后常驻不卸载。
/// J-Link DLL 内部有工作线程/TLS/全局状态，FreeLibrary 时机稍错即残留线程跳进已释放代码而崩溃
/// （JLinkExe / Ozone / pylink 均进程常驻）；断开连接只走 JLINK_Close（连接级复位），不动库。
/// DLL 不随应用分发（SEGGER 许可要求），运行时按 注册表 → 环境变量 → Program Files 定位。
/// 所有 J-Link API 非线程安全：跨线程调用必须持 <see cref="Gate"/> 串行。
/// </summary>
internal sealed class JLinkNative
{
    private const string DllName = "JLink_x64.dll";

    // RTT Control 命令码（JLinkRTTCommand：START=0/STOP=1，pylink 官方文档 + SEGGER 论坛实战签名印证）
    private const int CmdStart = 0;
    private const int CmdStop = 1;

    // JLinkHost.USB
    private const int HostIfUsb = 1;

    private static JLinkNative? _instance;
    private static readonly object InstanceLock = new();

    private readonly IntPtr _handle;
    private readonly DGetDllVersion _getDllVersion;
    private readonly DExecCommand _execCommand;
    private readonly DOpen _open;
    private readonly DConnect _connect;
    private readonly DClose _close;
    private readonly DRttControl _rttControl;
    private readonly DRttRead _rttRead;
    private readonly DRttWrite _rttWrite;
    private readonly DEmuGetList _emuGetList;
    private readonly DSetHookUnsecureDialog _setHook;

    /// <summary>被抑制的 J-Link 内部弹窗消息（DLL 内部线程触发，订阅方只许做字符串记录，
    /// 绝不碰锁/UI——回调可能在持有 DLL 内部锁的上下文进来，重入 Gate 会死锁）。</summary>
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
            _open = Bind<DOpen>("JLINKARM_Open");
            _connect = Bind<DConnect>("JLINK_Connect");
            _close = Bind<DClose>("JLINK_Close");
            _rttControl = Bind<DRttControl>("JLINK_RTTERMINAL_Control");
            _rttRead = Bind<DRttRead>("JLINK_RTTERMINAL_Read");
            _rttWrite = Bind<DRttWrite>("JLINK_RTTERMINAL_Write");
            _emuGetList = Bind<DEmuGetList>("JLINKARM_EMU_GetList");
            _setHook = Bind<DSetHookUnsecureDialog>("JLINKARM_SetHookUnsecureDialog");
        }
        catch
        {
            NativeLibrary.Free(_handle); // 初始化期回滚（运行期常驻不 Free 是另一回事）
            throw;
        }

        Version = _getDllVersion();

        // 弹窗抑制：装上回调后 DLL 出错走回调而非自己的 MessageBox（否则连接失败会弹原生对话框卡线程）。
        // 必须早于任何 JLINKARM_Open。回调用静态字段根持防 GC，static lambda 保证不捕获 this。
        _setHook(HookCallback);
        ExecCommand("HideDeviceSelectionDialog = 1"); // 兜底：hook 未必覆盖所有弹框类型
    }

    /// <summary>弹窗抑制回调（J-Link 内部线程触发）。静态字段根持防 GC（委托被回收后再被 native 调用 = 崩溃）。
    /// 方法组到含指针签名委托的转换须 unsafe 上下文，经工厂方法桥接。</summary>
    private static readonly DHookUnsecureDialog HookCallback = CreateHook();

    private static unsafe DHookUnsecureDialog CreateHook() => OnUnsecureDialog;

    /// <summary>只做字符串转发，try/catch 全包：异常穿越 native 边界 = 进程崩溃，取锁 = 潜在死锁。</summary>
    private static unsafe void OnUnsecureDialog(byte* msgPtr)
    {
        try
        {
            var msg = msgPtr != null ? Marshal.PtrToStringAnsi((IntPtr)msgPtr) : null;
            if (!string.IsNullOrEmpty(msg))
                Log?.Invoke(msg);
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
                    "未找到 SEGGER J-Link 软件（JLink_x64.dll）。已尝试：注册表 HKLM\\SOFTWARE\\SEGGER\\J-Link、" +
                    "环境变量 SEGGER_JLINK_ROOT_PATH、Program Files\\SEGGER\\JLink*。" +
                    "请从 SEGGER 官网安装免费的 J-Link 软件后重试。");
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

    /// <summary>执行 J-Link 命令（如 "device = STM32F407VG"）。ASCII 编码 + NUL 结尾——
    /// GetDelegateForFunctionPointer 路径没有自动 charset 转换，必须手动编码。&lt;0 为错误码。</summary>
    public int ExecCommand(string cmd)
    {
        var bytes = new byte[Encoding.ASCII.GetByteCount(cmd) + 1];
        Encoding.ASCII.GetBytes(cmd, bytes);
        unsafe { fixed (byte* p = bytes) return _execCommand(p, 0, 0); }
    }

    /// <summary>JLINKARM_Open：打开探针连接。&lt;0 为错误码。</summary>
    public int Open() => _open();

    /// <summary>JLINK_Connect：连接目标 CPU（阻塞可达数秒，须后台线程 + Gate）。&lt;0 为错误码。</summary>
    public int Connect() => _connect();

    /// <summary>JLINK_Close：关闭连接（连接级复位，DLL 常驻）。返回值忽略（各版本 void/int 不一）。</summary>
    public void CloseDll()
    {
        try { _close(); } catch { /* 关闭异常不影响状态复位 */ }
    }

    /// <summary>启动 RTT（JLINK_RTTERMINAL_Control START）。configBlockAddress=0 表示自动搜索 RAM。
    /// 0 成功，&lt;0 错误码（-2 = 控制块未找到）。</summary>
    public unsafe int RttStart(uint configBlockAddress)
    {
        // JLINK_RTTERMINAL_START_t = { u32 ConfigBlockAddress; u32 保留×3 }（16 字节，无 padding）
        uint* cfg = stackalloc uint[4];
        cfg[0] = configBlockAddress;
        cfg[1] = 0;
        cfg[2] = 0;
        cfg[3] = 0;
        return _rttControl(CmdStart, cfg);
    }

    /// <summary>停止 RTT（STOP）。断开路径调用，返回值忽略不了也要忽略——失败只能带错误上抛。</summary>
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

    /// <summary>DLL 定位链（优先级即顺序）：注册表 InstallPath → SEGGER_JLINK_ROOT_PATH → Program Files\SEGGER\JLink*（取最新）。</summary>
    private static string? LocateDll()
    {
        // 1. 注册表（64 位安装写 HKLM\SOFTWARE\SEGGER\J-Link，x64 进程读默认视图即可）
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\SEGGER\J-Link");
            if (key?.GetValue("InstallPath") is string dir
                && File.Exists(Path.Combine(dir, DllName)))
                return Path.Combine(dir, DllName);
        }
        catch
        {
            // 注册表不可读走下一条
        }

        // 2. 环境变量
        var env = Environment.GetEnvironmentVariable("SEGGER_JLINK_ROOT_PATH");
        if (!string.IsNullOrEmpty(env) && File.Exists(Path.Combine(env, DllName)))
            return Path.Combine(env, DllName);

        // 3. Program Files 通配（JLink、JLink_V794x 等目录名，倒序取最新版本）
        var segger = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "SEGGER");
        if (Directory.Exists(segger))
        {
            var hit = Directory.GetDirectories(segger, "JLink*")
                .Where(d => File.Exists(Path.Combine(d, DllName)))
                .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (hit is not null)
                return Path.Combine(hit, DllName);
        }
        return null;
    }

    // ---------- 委托声明（cdecl；x64 单一调用约定，与 stdcall 声明无差别） ----------
    // 全部存实例字段、实例被静态 _instance 根持：委托被 GC 回收后再被 native 调用 = 崩溃。

    private delegate int DGetDllVersion();
    private unsafe delegate int DExecCommand(byte* cmd, int a, int b);
    private delegate int DOpen();
    private delegate int DConnect();
    private delegate int DClose();
    private unsafe delegate int DRttControl(int cmd, void* cmdData);
    private unsafe delegate int DRttRead(int bufferIndex, byte* buf, int size);
    private unsafe delegate int DRttWrite(int bufferIndex, byte* buf, int len);
    private unsafe delegate int DEmuGetList(int hostIfs, uint* sns, int maxItems);
    private unsafe delegate void DHookUnsecureDialog(byte* msg);
    private unsafe delegate void DSetHookUnsecureDialog(DHookUnsecureDialog hook);
}
