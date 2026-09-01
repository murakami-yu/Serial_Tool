using SerialTool.Core.Framing;
using Xunit;

namespace SerialTool.Core.Tests;

public class AutoReplyTests
{
    /// <summary>Raw = AA 55 <cmd> <payload×2> <crc×2>；模板 T1。</summary>
    private static ParsedFrame Frame(byte cmd, params byte[] payload)
    {
        var raw = new byte[2 + 1 + payload.Length + 2];
        raw[0] = 0xAA; raw[1] = 0x55; raw[2] = cmd;
        payload.CopyTo(raw, 3);
        return new ParsedFrame(DateTime.Now, raw, Ok: true, Error: "",
            TemplateName: "T1", CommandOffset: 2, PayloadOffset: 3, PayloadLength: payload.Length);
    }

    private static AutoReplyRule Rule(
        string template = "", string cmd = "", string match = "",
        string reply = "05 06", bool enabled = true) => new()
    {
        Enabled = enabled,
        Template = template,
        CommandHex = cmd,
        MatchHex = match,
        ReplyHex = reply,
    };

    [Fact]
    public void Match_NoConditions_AnyOkFrameMatches()
        => Assert.NotNull(AutoReplyMatcher.Match(new[] { Rule() }, Frame(0x10, 0x11)));

    [Fact]
    public void Match_DisabledRule_Skipped()
        => Assert.Null(AutoReplyMatcher.Match(new[] { Rule(enabled: false) }, Frame(0x10, 0x11)));

    [Fact]
    public void Match_TemplateFilter()
    {
        var f = Frame(0x10, 0x11);
        Assert.NotNull(AutoReplyMatcher.Match(new[] { Rule(template: "T1") }, f));
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(template: "OTHER") }, f));
    }

    [Fact]
    public void Match_CommandPrefixFilter()
    {
        var f = Frame(0x10, 0x11);
        Assert.NotNull(AutoReplyMatcher.Match(new[] { Rule(cmd: "10") }, f));
        Assert.NotNull(AutoReplyMatcher.Match(new[] { Rule(cmd: "10 11") }, f)); // 前缀可跨域
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(cmd: "03") }, f));
    }

    [Fact]
    public void Match_SubstringAnywhere()
    {
        var f = Frame(0x10, 0x11); // Raw = AA 55 10 11 00 00
        Assert.NotNull(AutoReplyMatcher.Match(new[] { Rule(match: "AA 55") }, f)); // 帧头处
        Assert.NotNull(AutoReplyMatcher.Match(new[] { Rule(match: "11 00") }, f)); // payload+crc 处
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(match: "AA 56") }, f));
    }

    [Fact]
    public void Match_CommandAndSubstring_CombinedAsAnd()
    {
        var f = Frame(0x10, 0x11);
        Assert.NotNull(AutoReplyMatcher.Match(new[] { Rule(cmd: "10", match: "AA 55") }, f));
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(cmd: "03", match: "AA 55") }, f)); // 命令不符 → 不匹配
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(cmd: "10", match: "BB") }, f));   // 子串不符 → 不匹配
    }

    [Fact]
    public void Match_FirstEnabledRuleWins()
    {
        var rules = new[]
        {
            Rule(reply: "01"),
            Rule(reply: "02"), // 不可能到达（第一条已匹配）
        };
        Assert.Equal("01", AutoReplyMatcher.Match(rules, Frame(0x10))!.ReplyHex);
    }

    [Fact]
    public void Match_EmptyOrInvalidReply_Skipped()
    {
        var f = Frame(0x10, 0x11);
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(reply: "") }, f));       // 空回复
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(reply: "0") }, f));      // 奇数位
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(reply: "XY") }, f));     // 非法字符
    }

    [Fact]
    public void Match_InvalidFilterHex_Skipped()
    {
        var f = Frame(0x10, 0x11);
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(cmd: "GG") }, f));
        Assert.Null(AutoReplyMatcher.Match(new[] { Rule(match: "0") }, f));
    }
}
