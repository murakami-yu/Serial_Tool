using System.Text.Json;
using System.Text.Json.Serialization;
using SerialTool.Core.Checksum;

namespace SerialTool.Core.Framing;

/// <summary>
/// 帧结构中的一个字段。
/// 帧结构 = 帧头 + 字段链（按序） + 校验域 + 帧尾，字段链覆盖真实协议的任意布局：
/// 例如 Modbus 写帧 01 10 [addr2][qty2][len1] data CRC —— 长度域在帧中间。
/// </summary>
public sealed class FrameField
{
    /// <summary>字段类型：cmd=命令域 / fixed=固定域（寄存器地址等）/ length=长度域 / data=数据域。</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "data";

    /// <summary>cmd / fixed / length 的字节数（0~4）。</summary>
    [JsonPropertyName("size")] public int Size { get; set; }

    /// <summary>length 字段的字节序（true=大端）。</summary>
    [JsonPropertyName("bigEndian")] public bool BigEndian { get; set; }

    /// <summary>data 无长度域时的固定长度（配合 kind=data 且不扫描帧尾使用）。</summary>
    [JsonPropertyName("dataFixedSize")] public int DataFixedSize { get; set; }

    /// <summary>true = data 长度由帧尾扫描界定（无长度域协议，如 EM：... data CRC ED）。</summary>
    [JsonPropertyName("scanToFooter")] public bool ScanToFooter { get; set; }
}

/// <summary>协议帧模板（JSON 可编辑）。</summary>
public sealed class FrameTemplate
{
    [JsonPropertyName("name")] public string Name { get; set; } = "新协议";

    /// <summary>帧头，HEX 文本，如 "AA 55"（至少 1 字节）。</summary>
    [JsonPropertyName("header")] public string Header { get; set; } = "AA 55";

    /// <summary>帧尾，HEX 文本，空串表示无（无长度域协议必须配置帧尾）。</summary>
    [JsonPropertyName("footer")] public string Footer { get; set; } = "";

    /// <summary>字段链（帧头之后、校验域之前的结构）。</summary>
    [JsonPropertyName("fields")] public List<FrameField> Fields { get; set; } = new();

    /// <summary>校验算法：none / xor / sum8 / crc8 / crc16Modbus / crc16Ccitt / crc32。</summary>
    [JsonPropertyName("checksum")] public string Checksum { get; set; } = "crc16Modbus";

    /// <summary>校验值线上字节序：true = 高字节在前（CRCh CRCl），false = 低字节在前（Modbus 标准）。</summary>
    [JsonPropertyName("checksumBigEndian")] public bool ChecksumBigEndian { get; set; } = true;

    /// <summary>帧长上限（防假帧头导致超长等待），默认 512。</summary>
    [JsonPropertyName("maxFrameLength")] public int MaxFrameLength { get; set; } = 512;

    /// <summary>是否参与多模板并行解析。</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

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
        if (Fields.Count == 0)
            throw new FormatException("字段链不能为空");
        if (Fields.Count(f => f.Kind == "length") > 1)
            throw new FormatException("长度域至多一个");
        if (Fields.Count(f => f.Kind == "data") > 1)
            throw new FormatException("数据域至多一个");
        foreach (var f in Fields)
        {
            if (f.Kind is not ("cmd" or "fixed" or "length" or "data"))
                throw new FormatException($"未知字段类型: {f.Kind}");
            if (f.Kind != "data" && (f.Size < 1 || f.Size > 4))
                throw new FormatException($"{f.Kind} 字段字节数需 1~4");
        }
        var data = Fields.FirstOrDefault(f => f.Kind == "data");
        var hasLength = Fields.Any(f => f.Kind == "length");
        if (data is not null)
        {
            if (data.ScanToFooter && string.IsNullOrWhiteSpace(Footer))
                throw new FormatException("帧尾扫描模式需要配置帧尾");
            if (!hasLength && !data.ScanToFooter && data.DataFixedSize < 0)
                throw new FormatException("无长度域时数据域需定长或扫描帧尾");
        }
        if (MaxFrameLength is < 16 or > 4096)
            throw new FormatException("帧长上限需在 16~4096 之间");
    }

    /// <summary>v2 默认模板集（覆盖常见真实协议布局）。</summary>
    public static List<FrameTemplate> Samples() => new()
    {
        new FrameTemplate
        {
            Name = "Modbus 读请求(03) 定长",
            Header = "01",
            Fields =
            {
                new FrameField { Kind = "cmd", Size = 1 },
                new FrameField { Kind = "fixed", Size = 4 },      // 起始地址 + 寄存器数量
                new FrameField { Kind = "data", DataFixedSize = 0 },
            },
            Checksum = "crc16Modbus",
            ChecksumBigEndian = true,
        },
        new FrameTemplate
        {
            Name = "Modbus 写请求(10) 长度域在帧中间",
            Header = "01",
            Fields =
            {
                new FrameField { Kind = "cmd", Size = 1 },
                new FrameField { Kind = "fixed", Size = 4 },      // 起始地址 + 寄存器数量
                new FrameField { Kind = "length", Size = 1 },     // byteCount = 数据字节数
                new FrameField { Kind = "data" },
            },
            Checksum = "crc16Modbus",
            ChecksumBigEndian = true,
        },
        new FrameTemplate
        {
            Name = "Modbus 读响应(03) 长度域",
            Header = "01",
            Fields =
            {
                new FrameField { Kind = "cmd", Size = 1 },
                new FrameField { Kind = "length", Size = 1 },
                new FrameField { Kind = "data" },
            },
            Checksum = "crc16Modbus",
            ChecksumBigEndian = true,
        },
        new FrameTemplate
        {
            Name = "EM 协议 帧尾扫描",
            Header = "EA",
            Fields =
            {
                new FrameField { Kind = "fixed", Size = 2 },      // src<<8 | dst
                new FrameField { Kind = "cmd", Size = 1 },
                new FrameField { Kind = "data", ScanToFooter = true },
            },
            Checksum = "crc16Modbus",
            ChecksumBigEndian = true,
            Footer = "ED",
        },
    };

    /// <summary>v1 模板（长度域紧跟帧头模型）迁移为 v2 字段链。</summary>
    public static FrameTemplate? MigrateV1(JsonElement el)
    {
        if (el.TryGetProperty("fields", out _)) return null; // 已是 v2
        if (!el.TryGetProperty("lengthBytes", out _)) return null;

        int lengthBytes = el.TryGetProperty("lengthBytes", out var lb) && lb.ValueKind == JsonValueKind.Number ? lb.GetInt32() : 1;
        int cmdBytes = el.TryGetProperty("cmdBytes", out var cb) && cb.ValueKind == JsonValueKind.Number ? cb.GetInt32() : 1;
        int fixedPayload = el.TryGetProperty("fixedPayloadLength", out var fp) && fp.ValueKind == JsonValueKind.Number ? fp.GetInt32() : 0;
        bool lenBig = el.TryGetProperty("lengthBigEndian", out var be) && be.ValueKind == JsonValueKind.True;
        bool ckBig = el.TryGetProperty("checksumBigEndian", out var cb2) && cb2.ValueKind == JsonValueKind.True;

        var t = new FrameTemplate
        {
            Name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "迁移" : "迁移",
            Header = el.TryGetProperty("header", out var h) ? h.GetString() ?? "AA 55" : "AA 55",
            Footer = el.TryGetProperty("footer", out var f) ? f.GetString() ?? "" : "",
            Checksum = el.TryGetProperty("checksum", out var c) ? c.GetString() ?? "crc16Modbus" : "crc16Modbus",
            ChecksumBigEndian = ckBig,
            MaxFrameLength = el.TryGetProperty("maxFrameLength", out var m) && m.ValueKind == JsonValueKind.Number ? m.GetInt32() : 512,
        };
        if (cmdBytes > 0)
            t.Fields.Add(new FrameField { Kind = "cmd", Size = cmdBytes });
        if (lengthBytes > 0)
            t.Fields.Add(new FrameField { Kind = "length", Size = lengthBytes, BigEndian = lenBig });
        t.Fields.Add(lengthBytes > 0
            ? new FrameField { Kind = "data" }
            : new FrameField { Kind = "data", DataFixedSize = fixedPayload });
        return t;
    }
}
