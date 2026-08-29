using System.Text.Json.Serialization;
using SerialTool.Core.Checksum;

namespace SerialTool.Core.Framing;

/// <summary>
/// 帧格式模板（JSON 可编辑，新增设备协议零改码）。
/// 帧结构：[帧头][长度域][命令域][数据域][校验域][帧尾]
///  - 长度域取值 = 数据域字节数（不含其余部分）
///  - 校验域覆盖 = 帧头至数据域末尾（校验域之前的全部字节）
/// </summary>
public sealed class FrameTemplate
{
    [JsonPropertyName("name")] public string Name { get; set; } = "新协议";

    /// <summary>帧头，HEX 文本，如 "AA 55"（至少 1 字节）。</summary>
    [JsonPropertyName("header")] public string Header { get; set; } = "AA 55";

    /// <summary>帧尾，HEX 文本，空串表示无帧尾。</summary>
    [JsonPropertyName("footer")] public string Footer { get; set; } = "";

    /// <summary>长度域字节数：0 = 定长帧（用 FixedPayloadLength），1 或 2 = 变长。</summary>
    [JsonPropertyName("lengthBytes")] public int LengthBytes { get; set; } = 1;

    /// <summary>长度域大端（false = 小端，Modbus 习惯）。</summary>
    [JsonPropertyName("lengthBigEndian")] public bool LengthBigEndian { get; set; }

    /// <summary>命令域字节数（0 或 1），紧跟长度域。</summary>
    [JsonPropertyName("cmdBytes")] public int CmdBytes { get; set; } = 1;

    /// <summary>定长帧的数据域长度（LengthBytes=0 时生效）。</summary>
    [JsonPropertyName("fixedPayloadLength")] public int FixedPayloadLength { get; set; }

    /// <summary>校验算法：none / xor / sum8 / crc8 / crc16Modbus / crc16Ccitt / crc32。</summary>
    [JsonPropertyName("checksum")] public string Checksum { get; set; } = "crc16Modbus";

    /// <summary>帧长上限（防假帧头导致的超长等待），默认 512。</summary>
    [JsonPropertyName("maxFrameLength")] public int MaxFrameLength { get; set; } = 512;

    /// <summary>解析校验算法枚举（未知名称按 None 处理）。</summary>
    [JsonIgnore]
    public ChecksumAlgorithm ChecksumAlg => Checksum switch
    {
        "xor" => ChecksumAlgorithm.Xor,
        "sum8" => ChecksumAlgorithm.Sum8,
        "crc8" => ChecksumAlgorithm.Crc8,
        "crc16Modbus" => ChecksumAlgorithm.Crc16Modbus,
        "crc16Ccitt" => ChecksumAlgorithm.Crc16CcittFalse,
        "crc32" => ChecksumAlgorithm.Crc32,
        _ => ChecksumAlgorithm.None,
    };

    /// <summary>校验模板合法性，非法抛 FormatException（带原因）。</summary>
    public void Validate()
    {
        if (Hex.TryParse(Header, out var h) is false || h.Length == 0)
            throw new FormatException("帧头必须至少 1 字节合法 HEX");
        if (Hex.TryParse(Footer, out _) is false)
            throw new FormatException("帧尾 HEX 非法");
        if (LengthBytes is < 0 or > 2)
            throw new FormatException("长度域字节数只支持 0/1/2");
        if (CmdBytes is < 0 or > 1)
            throw new FormatException("命令域字节数只支持 0/1");
        if (LengthBytes == 0 && FixedPayloadLength < 0)
            throw new FormatException("定长帧的数据域长度不能为负");
        if (MaxFrameLength is < 16 or > 4096)
            throw new FormatException("帧长上限需在 16~4096 之间");
    }

    /// <summary>示例模板（首次启动种子）。</summary>
    public static FrameTemplate Sample() => new()
    {
        Name = "示例 AA-55 协议",
        Header = "AA 55",
        LengthBytes = 1,
        LengthBigEndian = false,
        CmdBytes = 1,
        Checksum = "crc16Modbus",
    };
}
