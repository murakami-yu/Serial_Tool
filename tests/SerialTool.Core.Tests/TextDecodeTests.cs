using System.Text;
using SerialTool.Core;
using Xunit;

namespace SerialTool.Core.Tests;

public class TextDecodeTests
{
    static TextDecodeTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void PureAscii_Passthrough()
    {
        var bytes = "Hello 123"u8.ToArray();
        Assert.Equal("Hello 123", TextDecode.ToDisplay(bytes));
    }

    [Fact]
    public void Utf8Chinese_Decoded()
    {
        var bytes = "育种测试"u8.ToArray();
        Assert.Equal("育种测试", TextDecode.ToDisplay(bytes));
    }

    [Fact]
    public void GbkChinese_Decoded()
    {
        // 设备发 GBK 编码中文（非法 UTF-8 → 走 GBK 分支）
        var gbk = Encoding.GetEncoding("GBK");
        var bytes = gbk.GetBytes("育种温度 25.6C");
        Assert.Equal("育种温度 25.6C", TextDecode.ToDisplay(bytes));
    }

    [Fact]
    public void ControlChars_Dots()
    {
        var bytes = new byte[] { 0x41, 0x01, 0x0D, 0x0A, 0x7F, 0x42 };
        Assert.Equal("A....B", TextDecode.ToDisplay(bytes));
    }

    [Fact]
    public void MixedChineseAndControl()
    {
        var gbk = Encoding.GetEncoding("GBK");
        // 0x01/0x00 在 GBK 中是单字节，不会破坏后续汉字对齐（0xAA 等前导字节会错位配对）
        var bytes = new byte[] { 0x01 }.Concat(gbk.GetBytes("种")).Concat(new byte[] { 0x00 }).ToArray();
        Assert.Equal(".种.", TextDecode.ToDisplay(bytes));
    }

    [Fact]
    public void EmptyInput()
        => Assert.Equal(string.Empty, TextDecode.ToDisplay(Array.Empty<byte>()));
}
