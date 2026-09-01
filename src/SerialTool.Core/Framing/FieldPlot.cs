using System.Text.Json.Serialization;

namespace SerialTool.Core.Framing;

/// <summary>
/// 帧字段曲线配置：从解析成功帧的数据域按 偏移+宽度 取整数原始值，乘缩放系数得物理量。
/// 模板/命令前缀用于多协议混流时挑出目标帧。
/// </summary>
public sealed class FieldPlotConfig
{
    [JsonPropertyName("name")] public string Name { get; set; } = "curve1";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>限定来源模板名，空 = 任意模板。</summary>
    [JsonPropertyName("template")] public string Template { get; set; } = "";

    /// <summary>命令域前缀过滤（HEX 文本），空 = 不过滤。</summary>
    [JsonPropertyName("commandHex")] public string CommandHex { get; set; } = "";

    /// <summary>数据域内字节偏移（0 起）。</summary>
    [JsonPropertyName("offset")] public int Offset { get; set; }

    /// <summary>取值宽度：1 / 2 / 4 字节。</summary>
    [JsonPropertyName("width")] public int Width { get; set; } = 2;

    /// <summary>true = 大端（高字节在前）。</summary>
    [JsonPropertyName("bigEndian")] public bool BigEndian { get; set; }

    /// <summary>true = 按有符号整数解释（补码符号扩展）。</summary>
    [JsonPropertyName("signed")] public bool Signed { get; set; }

    /// <summary>物理量换算：y = raw × scale。</summary>
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1.0;

    /// <summary>Y 轴单位标签（如 ℃、V）。</summary>
    [JsonPropertyName("unit")] public string Unit { get; set; } = "";
}

/// <summary>帧 → 曲线点求值（纯函数）。</summary>
public static class FieldPlotEvaluator
{
    /// <summary>
    /// 对一帧求值：模板/命令过滤通过后，从数据域取 Width 字节按字节序/符号解释，乘 Scale。
    /// 不匹配（模板名不同 / 命令前缀不符 / 取值范围越出数据域 / 宽度非法 / 命令 HEX 非法）返回 null。
    /// 调用方保证 f.Ok；配置对象求值期间不可变（App 层以快照方式提供）。
    /// </summary>
    public static double? Evaluate(FieldPlotConfig c, ParsedFrame f)
    {
        if (!c.Enabled) return null;
        if (c.Template.Length > 0 && !string.Equals(c.Template, f.TemplateName, StringComparison.Ordinal))
            return null;

        if (!Hex.TryParse(c.CommandHex, out var cmd)) return null;
        if (cmd.Length > 0)
        {
            if (f.CommandOffset < 0 || f.CommandOffset + cmd.Length > f.Raw.Length) return null;
            if (!f.Raw.AsSpan(f.CommandOffset, cmd.Length).SequenceEqual(cmd)) return null;
        }

        if (c.Width is not (1 or 2 or 4)) return null;
        var start = f.PayloadOffset + c.Offset;
        if (c.Offset < 0 || start + c.Width > f.PayloadOffset + f.PayloadLength) return null;

        long v = 0;
        if (c.BigEndian)
        {
            for (var i = 0; i < c.Width; i++)
                v = (v << 8) | f.Raw[start + i];
        }
        else
        {
            for (var i = 0; i < c.Width; i++)
                v |= (long)f.Raw[start + i] << (8 * i);
        }

        if (c.Signed)
        {
            var bits = c.Width * 8;
            if (bits < 64 && (v & (1L << (bits - 1))) != 0)
                v -= 1L << bits;
        }
        return v * c.Scale;
    }
}
