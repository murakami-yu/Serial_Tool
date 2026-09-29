using System.Globalization;
using System.IO;
using System.Text;

namespace SerialTool.App.Services;

/// <summary>
/// 软件运行日志：关键操作（启动/退出、连接/断开、发送失败、配置读写异常、后端错误中断、
/// SSH 指纹裁决、独立终端会话等）按天落盘，便于事后还原操作序列、定位「何时做了什么、哪步失败」。
/// 与 CrashLogger（崩溃/挂起转储）和 SessionLogger（用户主动开启的收发数据记录）互补：
/// 本日志始终开启、低开销、只记事件不记数据流。
/// 目录 Logs/app/app_yyyyMMdd.log；exe 目录不可写时回落 %LOCALAPPDATA%\SerialTool\Logs\app。
/// 首次解析目录时清理 30 天前的旧文件。任何写入失败静默忽略——日志绝不拖垮应用。
/// </summary>
public static class AppLog
{
    private const int RetentionDays = 30;

    private static readonly object Gate = new();
    private static string? _dir;
    private static string? _currentPath;
    private static string _currentDay = string.Empty;

    /// <summary>运行日志目录（供「打开目录」类功能使用）。</summary>
    public static string Dir => LogDir();

    /// <summary>常规事件（连接/断开/启动/退出等）。</summary>
    public static void Info(string message) => Write("INFO", message, null);

    /// <summary>可恢复异常（连接失败/发送失败/配置读写失败等）。</summary>
    public static void Warn(string message, Exception? ex = null) => Write("WARN", message, ex);

    /// <summary>错误中断（后端掉线/崩溃报告已写等）。</summary>
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder(message.Length + 48);
            sb.Append('[').Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append("] [").Append(level)
              .Append("] [T").Append(Environment.CurrentManagedThreadId).Append("] ")
              .Append(message);
            if (ex is not null)
                sb.Append(" | ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);

            lock (Gate)
            {
                // 跨天滚动：日期变化即换文件（按天一个文件，天然限大小）
                var day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (day != _currentDay || _currentPath is null)
                {
                    _currentPath = Path.Combine(LogDir(), $"app_{day}.log");
                    _currentDay = day;
                }
                File.AppendAllText(_currentPath, sb.ToString() + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* 日志自身绝不影响应用 */ }
    }

    private static string LogDir()
    {
        lock (Gate)
        {
            if (_dir is not null) return _dir;
            var dir = Path.Combine(AppContext.BaseDirectory, "Logs", "app");
            try
            {
                Directory.CreateDirectory(dir);
                var probe = Path.Combine(dir, ".probe");
                File.WriteAllText(probe, ""); File.Delete(probe);   // exe 旁可写性探测
            }
            catch
            {
                dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "SerialTool", "Logs", "app");
                Directory.CreateDirectory(dir);
            }
            _dir = dir;
            CleanupOld(dir);
            return dir;
        }
    }

    /// <summary>删除超过保留期的旧日志（尽力而为，失败忽略）。</summary>
    private static void CleanupOld(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-RetentionDays);
            foreach (var f in Directory.EnumerateFiles(dir, "app_*.log"))
            {
                if (File.GetLastWriteTime(f) < cutoff)
                    File.Delete(f);
            }
        }
        catch { }
    }
}
