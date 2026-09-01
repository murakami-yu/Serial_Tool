using SerialTool.Core;
using Xunit;

namespace SerialTool.Core.Tests;

public class RxFilterTests
{
    private static readonly byte[] Hay = { 0xAA, 0x55, 0x01, 0x02 };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNeedle_AlwaysMatches(string? needle)
        => Assert.True(RxFilter.IsMatch(needle, Hay));

    [Fact]
    public void EmptyHay_NeverMatches()
        => Assert.False(RxFilter.IsMatch("AA", Array.Empty<byte>()));

    [Theory]
    [InlineData("AA 55")]            // 完整前缀，含空格
    [InlineData("aa55")]             // 小写无分隔
    [InlineData("aa,55-01")]         // 混合分隔符
    [InlineData("5501")]             // 中段子串
    public void HexSubstring_Matches(string needle)
        => Assert.True(RxFilter.IsMatch(needle, Hay));

    [Theory]
    [InlineData("AB 55")]            // 首字节不匹配
    [InlineData("AA 56")]            // 尾字节不匹配
    [InlineData("0102AA")]           // 不连续（跨空格的字节序列不存在）
    public void HexNonSubstring_NoMatch(string needle)
        => Assert.False(RxFilter.IsMatch(needle, Hay));

    [Fact]
    public void OddHexDigits_FallsBackToTextMatch()
    {
        // "A" 是奇数位 HEX → 回退文本匹配；ToDisplay("AA 55 01 02") 无 '.' 字母 A，不命中
        Assert.False(RxFilter.IsMatch("A", Hay));
        // ASCII 文本子串命中
        Assert.True(RxFilter.IsMatch("hel", System.Text.Encoding.ASCII.GetBytes("hello")));
    }

    [Fact]
    public void TextNeedle_MatchesDecodedText()
    {
        _ = TextDecode.ToDisplay(Array.Empty<byte>()); // 触发静态构造：注册 GBK 编码提供程序
        var gbk = System.Text.Encoding.GetEncoding("GBK");
        Assert.True(RxFilter.IsMatch("育种", gbk.GetBytes("育种数据")));
        Assert.False(RxFilter.IsMatch("灌溉", gbk.GetBytes("育种数据")));
    }

    [Fact]
    public void NormalizeHex_StripsSeparatorsAndUppercases()
    {
        Assert.Equal("AA5501", RxFilter.NormalizeHex("aa 55,01"));
        Assert.Equal("", RxFilter.NormalizeHex(" - : "));
    }
}
