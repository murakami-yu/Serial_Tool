using System.IO;

namespace SerialTool.App.Services;

/// <summary>
/// 会话日志：将持续到达的收发数据行写入 txt 文件。
/// AutoFlush 保证异常退出（崩溃/拔线/断电）时不丢已写内容。
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private StreamWriter? _writer;

    /// <summary>当前是否处于写入状态。</summary>
    public bool IsActive => _writer is not null;

    /// <summary>当前日志文件路径（未开启时为 null）。</summary>
    public string? FilePath { get; private set; }

    /// <summary>打开（或切换）日志文件，追加模式。</summary>
    public void Open(string path)
    {
        Close();
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        _writer = new StreamWriter(path, append: true, System.Text.Encoding.UTF8)
        {
            AutoFlush = true,
        };
        FilePath = path;
    }

    /// <summary>写入一行（非活动时静默忽略）。</summary>
    public void WriteLine(string line) => _writer?.WriteLine(line);

    /// <summary>关闭日志文件，幂等。</summary>
    public void Close()
    {
        _writer?.Dispose();
        _writer = null;
    }

    public void Dispose() => Close();
}
