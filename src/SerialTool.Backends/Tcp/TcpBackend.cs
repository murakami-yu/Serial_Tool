using System.Net.Sockets;

namespace SerialTool.Backends.Tcp;

/// <summary>TCP 连接参数（串口服务器 / TCP 透传设备）。</summary>
public sealed record TcpConfig(string Host, int Port);

/// <summary>TCP 后端接口。</summary>
public interface ITcpBackend : IBusBackend
{
    void Open(TcpConfig cfg);
}

/// <summary>
/// TCP 透传后端：连接串口服务器（USR-TCP232、ESP8266 透传、ser2net 等）。
/// 与串口后端同构的事件流，收发/日志/多帧等上层功能全部复用。
/// </summary>
public sealed class TcpBackend : ITcpBackend
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private Thread? _readThread;
    private volatile bool _running;

    private const int ConnectTimeoutMs = 3000;

    public string Name => "TCP";
    public bool IsOpen => _running;

    public event EventHandler<TimedData>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>TCP 无本地设备可枚举。</summary>
    public IReadOnlyList<DeviceInfo> Scan() => Array.Empty<DeviceInfo>();

    public void Open(TcpConfig cfg)
    {
        Close();

        var client = new TcpClient();
        try
        {
            var async = client.BeginConnect(cfg.Host, cfg.Port, null, null);
            if (!async.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                throw new TimeoutException($"连接 {cfg.Host}:{cfg.Port} 超时（{ConnectTimeoutMs}ms）");
            client.EndConnect(async); // 拒绝/失败在此抛出
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
            Name = $"TcpRead:{cfg.Host}",
        };
        _readThread.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var stream = _stream;
        if (!_running || stream is null)
            throw new InvalidOperationException("TCP 连接未打开");
        var buf = data.ToArray();
        stream.Write(buf, 0, buf.Length);
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
                var data = new byte[n];
                Array.Copy(buf, data, n);
                DataReceived?.Invoke(this, new TimedData(DateTime.Now, data));
            }
            catch (Exception)
            {
                if (_running)
                    ErrorOccurred?.Invoke(this, "TCP 连接断开（对端关闭或网络中断）");
                break;
            }
        }
        _running = false;
    }
}
