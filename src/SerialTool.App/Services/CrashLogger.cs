using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SerialTool.App.Services;

/// <summary>
/// 崩溃/卡死诊断日志：全局异常捕获（UI 线程/后台线程/未观察 Task）+ UI 挂起看门狗。
/// 事件发生时写 Logs/crash/crash_yyyyMMdd_HHmmss_类型.log（异常全文 + 环境信息），并尽力写同名 .dmp
/// （dbghelp MiniDumpWriteDump 自转储：全部线程栈 + 模块——挂起死锁诊断的关键）。
/// exe 目录不可写时回落 %LOCALAPPDATA%\SerialTool\Logs\crash。
/// 挂起看门狗：后台线程每 2s 经 Dispatcher 回写心跳，超 15s 无心跳判挂起（阈值高于常见模态对话框操作时长，
/// 若日志发生时用户正开着文件选择框等系统对话框，需视为疑似误报）；调试器附加时不触发。
/// </summary>
public static class CrashLogger
{
    private const int HangThresholdSeconds = 15;

    private static string? _crashDir;
    private static int _dumpInFlight;                 // 防多事件并发写转储
    private static bool _fatalReported;               // 致命崩溃只报第一份（Dispatcher 未处理后运行时还会再抛 AppDomain，同一崩溃两份报告冗余）
    private static long _lastPongTicks = DateTime.UtcNow.Ticks;
    private static bool _hangDumped;                  // 每次运行挂起只转储一次（恢复后记一行）

    /// <summary>安装全部处理器 + 启动看门狗；在 App.OnStartup 最前调用。</summary>
    public static void Install()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteReport("后台线程未处理异常", e.ExceptionObject as Exception, withDump: true, isFatal: true);

        TaskScheduler.UnobservedTaskException += (_, e) =>
            WriteReport("未观察的 Task 异常", e.Exception, withDump: false);   // 不致命：只记文本

        if (Application.Current is not null)
        {
            Application.Current.DispatcherUnhandledException += (_, e) =>
                WriteReport("UI 线程未处理异常", e.Exception, withDump: true, isFatal: true);
            Application.Current.Exit += (_, _) => WriteSessionMarker("正常退出");
        }

        WriteSessionMarker("启动");
        StartWatchdog();
    }

    // ---------- 挂起看门狗 ----------

    private static void StartWatchdog()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        var t = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                try
                {
                    if (dispatcher.HasShutdownStarted) return;
                    // 心跳：UI 线程空闲即执行回写；UI 卡死则该委托永远排不上
                    dispatcher.BeginInvoke(() => Volatile.Write(ref _lastPongTicks, DateTime.UtcNow.Ticks));
                }
                catch { /* 关闭竞态忽略 */ }

                var silence = (DateTime.UtcNow.Ticks - Volatile.Read(ref _lastPongTicks)) / TimeSpan.TicksPerSecond;
                if (silence >= HangThresholdSeconds && !_hangDumped && !Debugger.IsAttached)
                {
                    _hangDumped = true;
                    WriteReport($"UI 线程挂起（{silence}s 无响应）",
                        new Exception("看门狗检出 UI 线程长时间无响应，转储中含全部线程栈，查 UI 线程（主线程）等待点即死锁位置"),
                        withDump: true);
                }
                else if (silence < 2 && _hangDumped)
                {
                    // 挂起后恢复：补记一行（同一份挂起日志追写，时间线完整）
                    AppendLine(LastReportPath, $"[{Now()}] UI 线程恢复响应");
                }
            }
        })
        { IsBackground = true, Name = "CrashWatchdog" };
        t.Start();
    }

    /// <summary>本次写出的最近一份报告路径（供恢复追记）。</summary>
    private static string? _lastReportPath;
    private static string? LastReportPath => _lastReportPath;

    // ---------- 报告写入 ----------

    /// <param name="isFatal">致命未处理异常（进程即将退出）才去重；挂起恢复后若再真崩仍要报。</param>
    private static void WriteReport(string kind, Exception? ex, bool withDump, bool isFatal = false)
    {
        try
        {
            if (isFatal && _fatalReported) return;    // 同一崩溃的多通道重报去重
            if (Interlocked.Exchange(ref _dumpInFlight, 1) == 1) return;  // 并发事件只写第一份
            if (isFatal) _fatalReported = true;
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            var basePath = Path.Combine(CrashDir(), $"crash_{stamp}_{Sanitize(kind)}");
            var logPath = basePath + ".log";
            _lastReportPath = logPath;

            var sb = new StringBuilder();
            sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"类型: {kind}");
            sb.AppendLine($"机器: {Environment.MachineName} / {Environment.OSVersion} / {(Environment.Is64BitOperatingSystem ? "x64" : "x86")}");
            sb.AppendLine($"运行时: {RuntimeInformation.FrameworkDescription}");
            try
            {
                var v = typeof(CrashLogger).Assembly.GetName().Version;
                sb.AppendLine($"版本: {v}");
            }
            catch { }
            sb.AppendLine($"进程: PID {Environment.ProcessId}，工作目录 {AppContext.BaseDirectory}");
            if (ex is not null)
            {
                sb.AppendLine();
                sb.AppendLine("===== 异常全文 =====");
                sb.AppendLine(ex.ToString());
            }
            sb.AppendLine();
            sb.AppendLine("请将本文件与同名 .dmp 一并发回诊断；.dmp 可用 Visual Studio / WinDbg 打开查看全部线程栈。");
            File.WriteAllText(logPath, sb.ToString(), Encoding.UTF8);

            if (withDump) TryWriteDump(basePath + ".dmp");
        }
        catch { /* 诊断自身绝不把应用搞崩 */ }
        finally { Volatile.Write(ref _dumpInFlight, 0); }
    }

    /// <summary>自转储（MiniDumpNormal + 间接引用内存：含全线程栈，体积数 MB 级）。失败只追加日志不抛。</summary>
    private static void TryWriteDump(string dmpPath)
    {
        try
        {
            using var fs = new FileStream(dmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
            // MiniDumpNormal | MiniDumpWithIndirectlyReferencedMemory
            const uint dumpType = 0x00000000 | 0x00000040;
            var ok = MiniDumpWriteDump(Process.GetCurrentProcess().Handle, (uint)Environment.ProcessId,
                fs.SafeFileHandle, dumpType, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            AppendLine(LastReportPath, $"[{Now()}] 转储写入{(ok ? "成功" : $"失败（Win32 错误 {Marshal.GetLastWin32Error()}）")}: {dmpPath}");
            if (!ok) TryDelete(dmpPath);
        }
        catch (Exception ex)
        {
            AppendLine(LastReportPath, $"[{Now()}] 转储写入异常：{ex.GetType().Name}: {ex.Message}");
            TryDelete(dmpPath);
        }
    }

    private static void WriteSessionMarker(string text)
    {
        try { File.AppendAllText(Path.Combine(CrashDir(), "session.log"), $"[{Now()}] {text} (PID {Environment.ProcessId}){Environment.NewLine}", Encoding.UTF8); }
        catch { }
    }

    private static void AppendLine(string? path, string line)
    {
        if (path is null) return;
        try { File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8); } catch { }
    }

    private static string CrashDir()
    {
        if (_crashDir is not null) return _crashDir;
        var dir = Path.Combine(AppContext.BaseDirectory, "Logs", "crash");
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".probe");
            File.WriteAllText(probe, ""); File.Delete(probe);   // exe 旁可写性探测
        }
        catch
        {
            dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SerialTool", "Logs", "crash");
            Directory.CreateDirectory(dir);
        }
        _crashDir = dir;
        return dir;
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s.Replace(' ', '_');
    }

    private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    [DllImport("dbghelp.dll", SetLastError = true)]
    private static extern bool MiniDumpWriteDump(IntPtr hProcess, uint processId,
        Microsoft.Win32.SafeHandles.SafeFileHandle hFile, uint dumpType,
        IntPtr exceptionParam, IntPtr userStreamParam, IntPtr callbackParam);
}
