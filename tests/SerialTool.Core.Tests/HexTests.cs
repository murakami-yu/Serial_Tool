using SerialTool.Core;
using Xunit;

namespace SerialTool.Core.Tests;

public class HexTests
{
    [Theory]
    [InlineData("AA 55 01", new byte[] { 0xAA, 0x55, 0x01 })]
    [InlineData("aa55 01", new byte[] { 0xAA, 0x55, 0x01 })]
    [InlineData("AA-55,0F\n01", new byte[] { 0xAA, 0x55, 0x0F, 0x01 })]
    [InlineData("Aa:b5", new byte[] { 0xAA, 0xB5 })]
    public void Parse_TolerantFormats(string input, byte[] expected)
    {
        Assert.True(Hex.TryParse(input, out var bytes));
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void Parse_Empty_ReturnsEmptyArray()
    {
        Assert.True(Hex.TryParse("", out var bytes));
        Assert.Empty(bytes);
        Assert.True(Hex.TryParse(null, out bytes));
        Assert.Empty(bytes);
    }

    [Theory]
    [InlineData("AA5")]        // 奇数位
    [InlineData("GG")]          // 非法字符
    [InlineData("AA 0G")]       // 混入非法字符
    [InlineData("0x55")]        // 前缀不支持
    public void Parse_Invalid_ReturnsFalse(string input)
        => Assert.False(Hex.TryParse(input, out _));

    [Fact]
    public void Encode_UpperCaseSpaceSeparated()
    {
        Assert.Equal("AA 55 01", Hex.Encode(new byte[] { 0xAA, 0x55, 0x01 }));
        Assert.Equal(string.Empty, Hex.Encode(Array.Empty<byte>()));
    }

    [Fact]
    public void Encode_RoundTrip_WithParse()
    {
        Assert.True(Hex.TryParse("AA 55 01", out var bytes));
        Assert.Equal("AA 55 01", Hex.Encode(bytes));
    }

    [Fact]
    public void ToAscii_PrintableAndDot()
    {
        Assert.Equal("A..z", Hex.ToAscii(new byte[] { 0x41, 0x01, 0x7F, 0x7A }));
        Assert.Equal(string.Empty, Hex.ToAscii(Array.Empty<byte>()));
    }
}
