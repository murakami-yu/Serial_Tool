using System.Text;

namespace SerialTool.Core;

/// <summary>
/// 串口数据文本解码：真实设备常发 GBK 编码中文（如"育种"），单字节 ASCII 视图会拆成乱码。
/// 策略：先试 UTF-8 严格解码，失败按 GBK 解码；控制字符与不可解字节显示 '.'。
/// </summary>
public static class TextDecode
{
    private static readonly Encoding Utf8Strict =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static Encoding? _gbk;

    static TextDecode()
    {
        // .NET 默认不含 GBK 等代码页编码，需注册 CodePages 提供程序
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    /// <summary>解码为可显示文本：控制字符与不可解字节显示 '.'。</summary>
    public static string ToDisplay(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return string.Empty;

        string decoded;
        try
        {
            decoded = Utf8Strict.GetString(data); // 纯 ASCII 走这里（UTF-8 兼容）
        }
        catch (DecoderFallbackException)
        {
            _gbk ??= Encoding.GetEncoding("GBK"); // 不可解字节产出 U+FFFD
            decoded = _gbk.GetString(data);
        }

        var sb = new StringBuilder(decoded.Length);
        foreach (var c in decoded)
        {
            if (char.IsControl(c) || c == '�')
                sb.Append('.');
            else
                sb.Append(c);
        }
        return sb.ToString();
    }
}
