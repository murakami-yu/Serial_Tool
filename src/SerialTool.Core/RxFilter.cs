namespace SerialTool.Core;

/// <summary>
/// 接收区视图层过滤：命中 = 规范化 HEX 串包含 或 显示文本包含。
/// 只影响视图渲染，数据缓冲与日志始终全量。
/// </summary>
public static class RxFilter
{
    /// <summary>规范化 HEX 匹配文本：去空格/逗号/横线/冒号分隔并转大写（与 Hex.Encode 输出同域）。</summary>
    public static string NormalizeHex(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c) && c is not ',' and not '-' and not ':')
                sb.Append(c);
        }
        return sb.ToString().ToUpperInvariant();
    }

    /// <summary>hay 是否命中过滤词（空/空白过滤词恒命中）。HEX 子串匹配忽略分隔与大小写，
    /// 奇数位或含非 HEX 字符的过滤词自动回退为纯文本匹配（与接收区文本显示内容比对）。</summary>
    public static bool IsMatch(string? needle, byte[] hay)
    {
        if (string.IsNullOrWhiteSpace(needle)) return true;
        if (hay.Length == 0) return false;

        var n = NormalizeHex(needle);
        if (n.Length > 0 && IsHexText(n) && (n.Length & 1) == 0)
        {
            var hayHex = NormalizeHex(Hex.Encode(hay));
            if (hayHex.Contains(n, StringComparison.Ordinal)) return true;
        }

        return TextDecode.ToDisplay(hay).Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHexText(string s)
    {
        foreach (var c in s)
        {
            if (!Uri.IsHexDigit(c)) return false;
        }
        return true;
    }
}
