using System.Text;
using SerialTool.Backends;
using SerialTool.Backends.Rtt;

// Serial_Tool RTT 后端端到端验证。
// 无硬件段（默认）：DLL 定位加载 / 弹窗抑制 / 探针枚举 / 参数校验负路径 / 无探针连接负路径
//   —— 只需安装 SEGGER J-Link 软件，无需探针与目标板。
// 真机段（--hw）：连接 / 读 / 写 / 互斥槽 / 断开幂等 / 槽释放后再连；--pull 追加拔探针自恢复。
//   —— 需 J-Link 探针 + 目标板（固件已集成 SEGGER RTT），真机验证项。
// 用法: RttCheck [--hw] [--pull] [device] [speedkHz]    默认 STM32F407VG 4000

var hw = args.Contains("--hw");
var pull = args.Contains("--pull");
var positional = args.Where(a => !a.StartsWith("--")).ToArray();
var device = positional.Length > 0 ? positional[0] : "STM32F407VG";
var speedKhz = positional.Length > 1 ? int.Parse(positional[1]) : 4000;

var failures = new List<string>();

void Check(bool ok, string label)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}");
    if (!ok) failures.Add(label);
}

void Skip(string label, string why) => Console.WriteLine($"[SKIP] {label}（{why}）");

RttBackend.JLinkLog += msg => Console.WriteLine($"  jlink-log: {msg}");

// ---- 1. DLL 定位与加载 ----
JLinkNative? jlink = null;
try
{
    jlink = JLinkNative.EnsureLoaded();
    Check(true, $"DLL 定位与加载：{jlink.DllPath} {jlink.VersionString}");
}
catch (Exception ex)
{
    // 未装 J-Link 软件：验证友好失败文案（含搜索链与安装指引）而非裸 FileNotFoundException
    var msg = ex.Message;
    Console.WriteLine($"  {ex.GetType().Name}: {msg}");
    Check(ex is DllNotFoundException
          && msg.Contains("JLink_x64.dll") && msg.Contains("SEGGER") && msg.Contains("官网"),
        "未装 J-Link 软件时给出含安装指引的友好失败");
}

// ---- 2. ExecCommand 通路（加载期已无任何 DLL 交互——hook 改到 Open 后装、签名三参 int 返回；
//         v1.4.12 单参 void 签名 = DLL 读垃圾按钮码 → 原生崩溃闪退，2026-10-07 真机根因）----
if (jlink is not null)
{
    var ret = jlink.ExecCommand("HideDeviceSelectionDialog = 1");
    Check(ret >= 0, $"ExecCommand 通路（HideDeviceSelectionDialog ret={ret}）");
}

// ---- 3. Scan 无害性（未装软件/无探针 → 空列表不抛）----
var backend = new RttBackend();
List<DeviceInfo> probes;
try
{
    probes = backend.Scan().ToList();
    Check(true, $"Scan() 无害性（探针 {probes.Count} 个：{string.Join(", ", probes.Select(p => p.DisplayName))}）");
}
catch (Exception ex)
{
    probes = new List<DeviceInfo>();
    Check(false, $"Scan() 抛异常: {ex.GetType().Name}: {ex.Message}");
}

// ---- 4. 参数校验负路径（纯托管，不触碰 DLL——用例 1 失败仍可跑）----
void ExpectInvalid(RttConfig cfg, string label)
{
    try
    {
        backend.Open(cfg);
        Check(false, $"{label}：未抛异常");
    }
    catch (InvalidOperationException ex)
    {
        Check(true, $"{label}：{ex.Message}");
    }
    catch (Exception ex)
    {
        Check(false, $"{label}：异常类型错误 {ex.GetType().Name}");
    }
}

ExpectInvalid(new RttConfig(""), "空器件名被拦");
ExpectInvalid(new RttConfig("STM32中文名"), "非 ASCII 器件名被拦");
ExpectInvalid(new RttConfig("STM32F407VG", 500), "速度越界（500kHz）被拦");
ExpectInvalid(new RttConfig("STM32F407VG", 60000), "速度越界（60000kHz）被拦");
ExpectInvalid(new RttConfig("STM32F407VG", Interface: 2), "接口值非法被拦");
ExpectInvalid(new RttConfig("STM32F407VG", Channel: 4), "通道越界被拦");

// ---- 5. 无探针连接负路径（连接失败应带错误码映射文案且不挂死）----
if (jlink is null)
{
    Skip("无探针连接负路径", "未装 J-Link 软件（Open 的失败文案已由用例 1 覆盖）");
}
else if (probes.Count == 0)
{
    try
    {
        backend.Open(new RttConfig(device, speedKhz));
        Check(false, "无探针时 Open 应抛异常");
    }
    catch (Exception ex)
    {
        Check(true, $"无探针时 Open 抛异常（{ex.Message}）");
        Check(!backend.IsOpen, "失败后 IsOpen == false");
    }
}
else
{
    Skip("无探针连接负路径", "本机已接探针，改走真机段");
}

// ================= 真机段（--hw）=================

if (!hw)
{
    Console.WriteLine(failures.Count == 0 ? "RESULT: ALL PASS" : $"RESULT: FAIL ({failures.Count} 项失败)");
    return failures.Count == 0 ? 0 : 1;
}

if (jlink is null)
{
    Skip("真机段全部", "未装 J-Link 软件");
    return 1;
}
if (probes.Count == 0)
{
    Skip("真机段全部", "未检测到探针");
    return 1;
}

var rx = new StringBuilder();
var errors = new List<string>();
backend.DataReceived += (_, d) => { lock (rx) rx.Append(Encoding.UTF8.GetString(d.Bytes)); };
backend.ErrorOccurred += (_, msg) =>
{
    lock (errors) errors.Add(msg);
    Console.WriteLine($"  error-event: {msg}");
};

// ---- 6. Open 全序列 ----
try
{
    backend.Open(new RttConfig(device, speedKhz));
    Check(true, $"Open 全序列（{device} SWD@{speedKhz}kHz ch0 自动搜索）");
}
catch (Exception ex)
{
    Check(false, $"Open 全序列: {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine(failures.Count == 0 ? "RESULT: ALL PASS" : $"RESULT: FAIL ({failures.Count} 项失败)");
    return 1;
}
Check(backend.IsOpen, "IsOpen == true");

// ---- 7. 读通路（3s 累计；固件需在通道 0 周期打印）----
Console.WriteLine("  读 3s ...");
Thread.Sleep(3000);
string snapshot;
lock (rx) snapshot = rx.ToString();
Console.WriteLine($"  raw-rx({snapshot.Length}B): [{snapshot.Replace("\r", "\\r").Replace("\n", "\\n")[..Math.Min(120, snapshot.Length)]}]");
Check(snapshot.Length > 0, $"读通路（3s 收到 {snapshot.Length}B）");

// ---- 8. 写通路（目标不消费输入时退避 2s 抛超时，消费则正常返回——两种都算通路验证）----
try
{
    backend.Write(Encoding.UTF8.GetBytes("rtt-check\n"));
    Check(true, "写通路（9B 下行无异常）");
}
catch (Exception ex)
{
    Check(false, $"写通路: {ex.Message}");
}

// ---- 9. 互斥槽（第一个会话开着时二次 Open 应被前置拦截）----
var second = new RttBackend();
try
{
    second.Open(new RttConfig(device, speedKhz));
    Check(false, "互斥槽：二次 Open 未被拦（危险：DLL 会静默改连目标）");
    second.Close();
}
catch (InvalidOperationException ex) when (ex.Message.Contains("只支持一个"))
{
    Check(true, "互斥槽：二次 Open 被拦");
}
catch (Exception ex)
{
    Check(false, $"互斥槽：异常类型错误 {ex.GetType().Name}: {ex.Message}");
}

// ---- 10. 正常断开幂等 ----
backend.Close();
backend.Close();
Check(!backend.IsOpen, "Close()×2 幂等且 IsOpen == false");
lock (errors) Check(errors.Count == 0, $"正常断开后无 ErrorOccurred（实际 {errors.Count} 个）");

// ---- 11. 槽释放后再连（DLL 常驻 + JLINK_Close 状态复位验证）----
var third = new RttBackend();
try
{
    third.Open(new RttConfig(device, speedKhz));
    Check(third.IsOpen, "槽释放后再连成功");
    lock (rx) rx.Clear();
    lock (errors) errors.Clear();

    // ---- 12. 拔探针自恢复（--pull；人工拔出，≤5s 应报中断并自关闭）----
    if (pull)
    {
        Console.WriteLine("  请在 5s 内拔出探针 ...");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && third.IsOpen)
            Thread.Sleep(100);
        Check(!third.IsOpen, "拔探针 ≤5s 自关闭（IsOpen == false）");
        lock (errors) Check(errors.Count > 0, $"拔探针报 ErrorOccurred（{errors.FirstOrDefault() ?? "无"}）");
        try
        {
            var after = third.Scan();
            Check(true, $"自恢复后 Scan 无异常（探针 {after.Count} 个）");
        }
        catch (Exception ex)
        {
            Check(false, $"自恢复后 Scan 抛异常: {ex.Message}");
        }
    }
    third.Close();
}
catch (Exception ex)
{
    Check(false, $"槽释放后再连: {ex.GetType().Name}: {ex.Message}");
}

Console.WriteLine(failures.Count == 0 ? "RESULT: ALL PASS" : $"RESULT: FAIL ({failures.Count} 项失败)");
return failures.Count == 0 ? 0 : 1;
