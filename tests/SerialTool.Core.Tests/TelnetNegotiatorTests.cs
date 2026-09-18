using SerialTool.Backends.Telnet;
using Xunit;

namespace SerialTool.Core.Tests;

/// <summary>Telnet IAC 协商器：净数据透传 / 转义 / 协商应答 / 子协商丢弃 / 跨包分片。</summary>
public class TelnetNegotiatorTests
{
    private static byte[] B(params byte[] b) => b;

    [Fact]
    public void 普通数据原样透传()
    {
        var n = new TelnetNegotiator();
        var (data, replies) = n.Feed(B(1, 2, 3, (byte)'A', 10, 13));
        Assert.Equal(new byte[] { 1, 2, 3, (byte)'A', 10, 13 }, data);
        Assert.Empty(replies);
    }

    [Fact]
    public void IAC_IAC_解为单个FF数据()
    {
        var n = new TelnetNegotiator();
        var (data, replies) = n.Feed(B(0x41, 255, 255, 0x42));
        Assert.Equal(B(0x41, 255, 0x42), data);
        Assert.Empty(replies);
    }

    [Fact]
    public void WILL_ECHO与SGA_回DO_其余回DONT()
    {
        var n = new TelnetNegotiator();
        var (_, r1) = n.Feed(B(255, 251, 1));   // WILL ECHO
        Assert.Equal(B(255, 253, 1), r1);        // DO ECHO
        var (_, r2) = n.Feed(B(255, 251, 3));    // WILL SGA
        Assert.Equal(B(255, 253, 3), r2);        // DO SGA
        var (_, r3) = n.Feed(B(255, 251, 24));   // WILL TTYPE
        Assert.Equal(B(255, 254, 24), r3);       // DONT TTYPE
    }

    [Fact]
    public void DO一律回WONT_WONT与DONT忽略()
    {
        var n = new TelnetNegotiator();
        var (_, r1) = n.Feed(B(255, 253, 31));   // DO NAWS
        Assert.Equal(B(255, 252, 31), r1);        // WONT NAWS
        var (_, r2) = n.Feed(B(255, 252, 1));    // WONT ECHO（对端撤销）
        Assert.Empty(r2);
        var (_, r3) = n.Feed(B(255, 254, 1));    // DONT ECHO
        Assert.Empty(r3);
    }

    [Fact]
    public void 子协商SB到SE整体丢弃_内部数据不泄出()
    {
        var n = new TelnetNegotiator();
        var (data, replies) = n.Feed(B(0x41, 255, 250, 24, 0, (byte)'F', 255, 240, 0x42));
        // IAC SB TTYPE IS 'F' IAC SE —— 子协商内容丢弃，前后数据保留
        Assert.Equal(B(0x41, 0x42), data);
        Assert.Empty(replies);
    }

    [Fact]
    public void NOP与GA等单字节命令忽略()
    {
        var n = new TelnetNegotiator();
        var (data, _) = n.Feed(B(0x41, 255, 241, 0x42));   // IAC NOP
        Assert.Equal(B(0x41, 0x42), data);
        var (data2, _) = n.Feed(B(255, 249));               // IAC GA
        Assert.Empty(data2);
    }

    [Fact]
    public void IAC序列跨包分片_状态保持()
    {
        var n = new TelnetNegotiator();
        var (d1, _) = n.Feed(B(255));            // 只有 IAC
        Assert.Empty(d1);
        var (d2, r2) = n.Feed(B(251));            // 补齐 = WILL ...
        Assert.Empty(d2);
        var (d3, r3) = n.Feed(B(1));             // 补齐选项 = ECHO
        Assert.Empty(d3);
        Assert.Equal(B(255, 253, 1), r3);

        // 子协商跨包：SB … (断) … IAC SE
        var n2 = new TelnetNegotiator();
        Assert.Empty(n2.Feed(B(255, 250, 24)).Data);
        Assert.Empty(n2.Feed(B(1, 2, 3)).Data);
        var (d, _) = n2.Feed(B(255, 240, 0x58));
        Assert.Equal(B(0x58), d);
    }

    [Fact]
    public void 输出转义_FF翻倍_无FF零拷贝路径()
    {
        var escaped = TelnetNegotiator.Escape(B(0x41, 255, 0x42));
        Assert.Equal(B(0x41, 255, 255, 0x42), escaped);
        var plain = TelnetNegotiator.Escape(B(1, 2, 3));
        Assert.Equal(B(1, 2, 3), plain);
    }

    [Fact]
    public void 协商字节混在数据流中_两侧数据都保留()
    {
        var n = new TelnetNegotiator();
        var (data, replies) = n.Feed(B((byte)'l', (byte)'s', 10, 255, 253, 1, (byte)'o', (byte)'k'));
        // ls\r 后跟 DO ECHO 协商，随后 ok —— 数据 ls\nok 保留，回 WONT ECHO
        Assert.Equal(B((byte)'l', (byte)'s', 10, (byte)'o', (byte)'k'), data);
        Assert.Equal(B(255, 252, 1), replies);
    }
}
