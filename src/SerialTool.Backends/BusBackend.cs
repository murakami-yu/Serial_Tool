namespace SerialTool.Backends;

/// <summary>设备枚举信息（总线无关）。</summary>
/// <param name="Id">后端内部使用的设备标识（如 "COM3"）。</param>
/// <param name="DisplayName">UI 显示名（可含厂商/描述）。</param>
public sealed record DeviceInfo(string Id, string DisplayName);

/// <summary>统一数据事件：时间戳 + 原始字节，三类总线同构。</summary>
public sealed record TimedData(DateTime Timestamp, byte[] Bytes);

/// <summary>
/// 硬件后端统一接口（UART / TCP / I2C / CAN 插拔式扩展）。
/// 总线特有的打开参数定义在各自的后端接口（如 <see cref="Serial.ISerialBackend"/>、<see cref="Tcp.ITcpBackend"/>）。
/// </summary>
public interface IBusBackend : IDisposable
{
    /// <summary>后端名称（如 "Serial" / "TCP" / "I2C" / "CAN"）。</summary>
    string Name { get; }

    /// <summary>当前是否已打开。</summary>
    bool IsOpen { get; }

    /// <summary>枚举当前可用设备。</summary>
    IReadOnlyList<DeviceInfo> Scan();

    /// <summary>接收数据事件流（读取线程触发，订阅方自行 marshal 到 UI 线程）。</summary>
    event EventHandler<TimedData>? DataReceived;

    /// <summary>错误/异常中断通知（如设备拔出、连接断开）。</summary>
    event EventHandler<string>? ErrorOccurred;

    /// <summary>写入数据（各总线通用）。</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>关闭设备，幂等。</summary>
    void Close();
}
