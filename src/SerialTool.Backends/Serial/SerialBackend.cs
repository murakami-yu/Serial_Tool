using System.Management;
using System.Text.RegularExpressions;
using RJCP.IO.Ports;

namespace SerialTool.Backends.Serial;

/// <summary>串口打开参数。</summary>
public sealed record SerialPortConfig(
    string PortName,
    int BaudRate,
    int DataBits,
    SerialParity Parity,
    SerialStopBits StopBits);

public enum SerialParity { None, Even, Odd }
public enum SerialStopBits { One, OnePointFive, Two }

/// <summary>串口后端接口（参数为串口特有；Write 由基接口提供）。</summary>
public interface ISerialBackend : IBusBackend
{
    void Open(SerialPortConfig cfg);
}

/// <summary>
/// UART / RS232 / RS485 后端（V1）。
/// 三种电气形态对软件透明：同一串口配置，差别仅在外部电平/接线。
/// 读取模型：独立后台线程阻塞读 + 超时轮询，事件抛出 {Timestamp, Bytes}。
/// </summary>
public sealed class SerialBackend : ISerialBackend
{
    private SerialPortStream? _port;
    private Thread? _readThread;
    private volatile bool _running;

    public string Name => "Serial";
    public bool IsOpen => _port is { IsOpen: true };

    public event EventHandler<TimedData>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;

    /// <summary>枚举系统串口（COMx），附带 WMI 友好设备名（如 "USB-SERIAL CH340"）。</summary>
    public IReadOnlyList<DeviceInfo> Scan()
    {
        var names = new SerialPortStream().GetPortNames();
        var friendly = LoadFriendlyNames();
        return names
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => friendly.TryGetValue(p, out var desc)
                ? new DeviceInfo(p, $"{p} · {desc}")
                : new DeviceInfo(p, p))
            .ToList();
    }

    /// <summary>WMI 查询 PnP 设备名，提取 COM 口 → 设备描述映射（如 "USB-SERIAL CH340 (COM3)"）。
    /// WMI 不可用时返回空映射，仅显示 COM 号。</summary>
    private static IReadOnlyDictionary<string, string> LoadFriendlyNames()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"] as string;
                if (string.IsNullOrEmpty(name)) continue;
                var m = Regex.Match(name, @"\((COM\d+)\)");
                if (!m.Success) continue;
                var desc = name.Replace(m.Value, "").Trim();
                map[m.Groups[1].Value] = desc.Length > 0 ? desc : name;
            }
        }
        catch
        {
            // WMI 异常（权限/服务不可用）时静默降级
        }
        return map;
    }

    public void Open(SerialPortConfig cfg)
    {
        Close();

        var port = new SerialPortStream(
            cfg.PortName, cfg.BaudRate, cfg.DataBits,
            MapParity(cfg.Parity), MapStopBits(cfg.StopBits))
        {
            ReadTimeout = 200,   // ms，超时轮询周期
            WriteTimeout = 3000, // ms
        };
        port.Open();

        _port = port;
        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"SerialRead:{cfg.PortName}",
        };
        _readThread.Start();
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        var port = _port;
        if (port is not { IsOpen: true })
            throw new InvalidOperationException("端口未打开");
        var buf = data.ToArray();
        port.Write(buf, 0, buf.Length);
    }

    public void Close()
    {
        _running = false;
        var port = _port;
        if (port is null) return;

        try { port.Close(); } catch { /* 关闭异常不影响状态复位 */ }
        _port = null;
        _readThread?.Join(500);
        _readThread = null;
    }

    public void Dispose() => Close();

    /// <summary>阻塞读 + 200ms 超时轮询；端口关闭/拔出时退出。</summary>
    private void ReadLoop()
    {
        var port = _port!;
        var buf = new byte[8192];
        while (_running)
        {
            try
            {
                var n = port.Read(buf, 0, buf.Length);
                if (n <= 0) continue;
                var data = new byte[n];
                Array.Copy(buf, data, n);
                DataReceived?.Invoke(this, new TimedData(DateTime.Now, data));
            }
            catch (TimeoutException)
            {
                // 正常超时轮询
            }
            catch (Exception)
            {
                if (_running)
                    ErrorOccurred?.Invoke(this, $"串口读取中断（{port.PortName} 被占用或设备已拔出）");
                break;
            }
        }
    }

    private static Parity MapParity(SerialParity p) => p switch
    {
        SerialParity.Even => Parity.Even,
        SerialParity.Odd => Parity.Odd,
        _ => Parity.None,
    };

    private static StopBits MapStopBits(SerialStopBits s) => s switch
    {
        SerialStopBits.Two => StopBits.Two,
        SerialStopBits.OnePointFive => StopBits.One5,
        _ => StopBits.One,
    };
}
