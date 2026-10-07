using System.IO;

namespace SerialTool.App.Services;

/// <summary>
/// 会话日志：将持续到达的收发数据行写入 txt 文件。
/// AutoFlush 保证异常退出（崩溃/拔线/断电）时不丢已写内容。
/// 内部锁防护读线程写入与 UI 线程关闭的竞态（终端会话日志在读线程实时写入）。
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private readonly object _gate = new();
    private StreamWriter? _writer;

    /// <summary>当前是否处于写入状态。</summary>
    public bool IsActive { get { lock (_gate) return _writer is not null; } }

    /// <summary>当前日志文件路径（未开启时为 null）。</summary>
    public string? FilePath { get; private set; }

    /// <summary>打开（或切换）日志文件，追加模式。</summary>
    public void Open(string path)
    {
        Close();
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        lock (_gate)
        {
            _writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            };
            FilePath = path;
        }
    }

    /// <summary>写入一行（非活动时静默忽略）。</summary>
    public void WriteLine(string line) { lock (_gate) _writer?.WriteLine(line); }

    /// <summary>写入原始文本（不追加换行；终端字节流按解码结果原样落盘）。</summary>
    public void Write(string text) { lock (_gate) _writer?.Write(text); }

    /// <summary>关闭日志文件，幂等。</summary>
    public void Close()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Dispose() => Close();
}
