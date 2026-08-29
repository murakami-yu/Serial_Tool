namespace SerialTool.Core;

/// <summary>HEX / ASCII 数据转换工具（发送解析与接收显示共用）。</summary>
public static class Hex
{
    /// <summary>
    /// 宽容解析 HEX 文本：容忍空格/换行/逗号/横线/冒号分隔；
    /// 空文本解析为空数组；奇数位或非法字符返回 false。
    /// </summary>
    public static bool TryParse(string? text, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrEmpty(text)) return true;

        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && c is not ',' and not '-' and not ':')
                sb.Append(c);
        }
        var s = sb.ToString();
        if (s.Length == 0) return true;
        if ((s.Length & 1) != 0) return false;

        var result = new byte[s.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2),
                    System.Globalization.NumberStyles.HexNumber, null, out result[i]))
                return false;
        }
        bytes = result;
        return true;
    }

    /// <summary>编码为 "AA 55 01" 形式（大写、空格分隔）。</summary>
    public static string Encode(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;
        var chars = new char[data.Length * 3 - 1];
        const string digits = "0123456789ABCDEF";
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            chars[i * 3] = digits[b >> 4];
            chars[i * 3 + 1] = digits[b & 0x0F];
            if (i < data.Length - 1) chars[i * 3 + 2] = ' ';
        }
        return new string(chars);
    }

    /// <summary>按可打印 ASCII 显示，不可见字符（0x20-0x7E 之外）显示为 '.'。</summary>
    public static string ToAscii(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;
        var chars = new char[data.Length];
        for (var i = 0; i < data.Length; i++)
            chars[i] = data[i] is >= 0x20 and < 0x7F ? (char)data[i] : '.';
        return new string(chars);
    }
}
