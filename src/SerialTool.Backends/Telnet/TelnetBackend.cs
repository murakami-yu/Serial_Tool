using System.Net.Sockets;

namespace SerialTool.Backends.Telnet;

/// <summary>Telnet 连接参数。</summary>
public sealed record TelnetConfig(string Host, int Port);

/// <summary>Telnet 后端接口。</summary>
public interface ITelnetBackend : IBusBackend
{
    void Open(TelnetConfig cfg);
}

/// <summary>
/// Telnet 后端（TCP + RFC 854 IAC 协商）：接收方向剥离协商序列产出净数据，
/// 按协商器策略自动回写应答（接受服务器 ECHO/SGA，其余拒绝）；发送方向 0xFF 转义。
/// 与串口/TCP/SSH 后端同构的事件流。
/// </summary>
public sealed class TelnetBackend : ITelnetBackend
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _readThread;
    private volatile bool _running;
    private readonly TelnetNegotiator _neg = new();

    private const int ConnectTimeoutMs = 5000;

    public string Name => "Telnet";
    public bool IsOpen => _running;

    public event EventHandler<TimedData>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>Telnet 无本地设备可枚举。</summary>
    public IReadOnlyList<DeviceInfo> Scan() => Array.Empty<DeviceInfo>();

    public void Open(TelnetConfig cfg)
    {
        Close();

        var client = new TcpClient();
        try
        {
            var async = client.BeginConnect(cfg.Host, cfg.Port, null, null);
            if (!async.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                throw new TimeoutException($"连接 {cfg.Host}:{cfg.Port} 超时（{ConnectTimeoutMs}ms）");
            client.EndConnect(async);
        }
        catch
        {
            client.Close();
            throw;
        }

        client.NoDelay = true;
        _client = client;
        _stream = client.GetStream();
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"TelnetRead:{cfg.Host}",
        };
        _readThread.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var stream = _stream;
        if (!_running || stream is null)
            throw new InvalidOperationException("Telnet 连接未打开");
        var escaped = TelnetNegotiator.Escape(data);
        stream.Write(escaped, 0, escaped.Length);
    }

    public void Close()
    {
        _running = false;
        var client = _client;
        if (client is null) return;

        try { client.Close(); } catch { /* 关闭异常不影响状态复位 */ }
        _client = null;
        _stream = null;
        _readThread?.Join(500);
        _readThread = null;
    }

    public void Dispose() => Close();

    private void ReadLoop()
    {
        var stream = _stream!;
        var buf = new byte[8192];
        while (_running)
        {
            try
            {
                var n = stream.Read(buf, 0, buf.Length);
                if (n <= 0) break; // 对端正常关闭
                var (data, replies) = _neg.Feed(buf.AsSpan(0, n));
                if (replies.Length > 0)
                    stream.Write(replies, 0, replies.Length); // 协商应答（读线程直写，量小）
                if (data.Length > 0)
                    DataReceived?.Invoke(this, new TimedData(DateTime.Now, data));
            }
            catch (Exception)
            {
                if (_running)
                    ErrorOccurred?.Invoke(this, "Telnet 连接断开（对端关闭或网络中断）");
                break;
            }
        }
        _running = false;
    }
}
