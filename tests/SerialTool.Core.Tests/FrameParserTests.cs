using SerialTool.Core.Checksum;
using SerialTool.Core.Framing;
using Xunit;

namespace SerialTool.Core.Tests;

public class FrameParserTests
{
    /// <summary>HEX 文本转字节（非法即抛）。</summary>
    private static byte[] H(string s)
        => SerialTool.Core.Hex.TryParse(s, out var b) && b.Length > 0
            ? b
            : throw new FormatException($"HEX 非法: {s}");

    /// <summary>按模板构造一帧：AA 55 | len | cmd | payload | crc16Modbus(小端)。</summary>
    private static byte[] Build(FrameTemplate t, byte cmd, byte[] payload)
    {
        var body = new List<byte>();
        body.AddRange(H(t.Header));
        if (t.LengthBytes >= 1) body.Add((byte)(payload.Length & 0xFF));
        if (t.LengthBytes == 2) body.Add((byte)(payload.Length >> 8)); // 小端：低字节在前
        if (t.CmdBytes > 0) body.Add(cmd);
        body.AddRange(payload);
        body.AddRange(Checksums.Compute(t.ChecksumAlg, body.ToArray()));
        if (t.Footer.Length > 0) body.AddRange(H(t.Footer));
        return body.ToArray();
    }

    private static List<ParsedFrame> Run(FrameTemplate t, params byte[][] chunks)
    {
        var parser = new FrameParser(t);
        var frames = new List<ParsedFrame>();
        parser.FrameEmitted += frames.Add;
        foreach (var c in chunks)
            parser.Feed(c);
        return frames;
    }

    private static FrameTemplate Sample() => FrameTemplate.Sample();

    // ---------- 基本解析 ----------

    [Fact]
    public void CompleteFrame_SingleChunk()
    {
        var f = Run(Sample(), Build(Sample(), 0x01, new byte[] { 0x11, 0x22, 0x33 }));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(3, f[0].CommandOffset);        // AA(0) 55(1) len(2) → cmd(3)
        Assert.Equal(4, f[0].PayloadOffset);        // cmd 后
        Assert.Equal(3, f[0].PayloadLength);
        Assert.Equal(new byte[] { 0xAA, 0x55, 0x03, 0x01, 0x11, 0x22, 0x33 }, f[0].Raw[..7]);
    }

    [Fact]
    public void HalfPacket_ByteByByteFeed()
    {
        var frame = Build(Sample(), 0x02, new byte[] { 0xAA, 0xBB });
        // 逐字节喂入（极端半包）
        var chunks = frame.Select(b => new[] { b }).ToArray();
        var f = Run(Sample(), chunks);
        Assert.Single(f);
        Assert.True(f[0].Ok);
    }

    [Fact]
    public void StickyPackets_TwoFramesOneChunk()
    {
        var t = Sample();
        var both = Build(t, 0x01, new byte[] { 0x01 })
            .Concat(Build(t, 0x02, new byte[] { 0x02, 0x03 }))
            .ToArray();
        var f = Run(t, both);
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.True(x.Ok));
        Assert.Equal(1, f[0].PayloadLength);
        Assert.Equal(2, f[1].PayloadLength);
    }

    [Fact]
    public void LeadingGarbage_SkippedAndCounted()
    {
        var t = Sample();
        var data = new byte[] { 0x00, 0x11, 0xAA /*假头前缀*/ }
            .Concat(Build(t, 0x01, new byte[] { 0x55 }))
            .ToArray();
        var parser = new FrameParser(t);
        var frames = new List<ParsedFrame>();
        parser.FrameEmitted += frames.Add;
        parser.Feed(data);
        Assert.Single(frames);
        Assert.True(frames[0].Ok);
        Assert.Equal(3, parser.DroppedBytes); // 00 11 AA 被丢弃
    }

    // ---------- 错误处理与重同步 ----------

    [Fact]
    public void BadChecksum_EmitsErrorThenResync()
    {
        var t = Sample();
        var good = Build(t, 0x01, new byte[] { 0x01 });
        var bad = Build(t, 0x02, new byte[] { 0x02 });
        bad[^1] ^= 0xFF; // 破坏校验

        var f = Run(t, bad.Concat(good).ToArray());
        Assert.Equal(2, f.Count);
        Assert.False(f[0].Ok);
        Assert.Contains("校验错误", f[0].Error);
        Assert.True(f[1].Ok); // 重同步后正确解出后一帧
    }

    [Fact]
    public void FalseHeader_HugeLength_Skipped()
    {
        var t = Sample();
        t.MaxFrameLength = 16; // 使 0xFF 长度超过上限 → 判假帧头
        // AA 55 + 长度 0xFF（超上限）→ 假帧头，后跟真帧
        var falseHeader = new byte[] { 0xAA, 0x55, 0xFF, 0x01 };
        var f = Run(t, falseHeader.Concat(Build(t, 0x03, new byte[] { 0x99 })).ToArray());
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(0x03, f[0].Raw[f[0].CommandOffset]);
    }

    [Fact]
    public void FooterMismatch_EmitsError()
    {
        var t = Sample();
        t.Footer = "0D 0A";
        var good = Build(t, 0x01, new byte[] { 0x01 });
        // 破坏帧尾
        good[^1] = 0x0B;
        var f = Run(t, good);
        Assert.Single(f);
        Assert.False(f[0].Ok);
        Assert.Contains("帧尾", f[0].Error);
    }

    // ---------- 变体模板 ----------

    [Fact]
    public void FixedLength_NoLengthField()
    {
        var t = new FrameTemplate
        {
            Name = "定长",
            Header = "55",
            LengthBytes = 0,
            CmdBytes = 0,
            FixedPayloadLength = 2,
            Checksum = "xor",
        };
        var frame = new byte[] { 0x55, 0x12, 0x34 };
        frame = frame.Concat(Checksums.Compute(ChecksumAlgorithm.Xor, frame)).ToArray();
        var f = Run(t, frame);
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(-1, f[0].CommandOffset);
        Assert.Equal(1, f[0].PayloadOffset);
        Assert.Equal(2, f[0].PayloadLength);
    }

    [Fact]
    public void TwoByteLength_LittleEndian()
    {
        var t = Sample();
        t.LengthBytes = 2;
        t.LengthBigEndian = false;
        var payload = new byte[300]; // 长度 300 = 0x012C → 小端 2C 01
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var f = Run(t, Build(t, 0x10, payload));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(300, f[0].PayloadLength);
    }

    [Fact]
    public void NoChecksum_PassesThrough()
    {
        var t = new FrameTemplate
        {
            Name = "无校验",
            Header = "AA",
            LengthBytes = 1,
            CmdBytes = 0,
            Checksum = "none",
        };
        var frame = new byte[] { 0xAA, 0x02, 0x12, 0x34 };
        var f = Run(t, frame);
        Assert.Single(f);
        Assert.True(f[0].Ok);
    }

    // ---------- 模板校验 ----------

    [Fact]
    public void TemplateValidate_BadHeader_Throws()
    {
        var t = Sample();
        t.Header = "GG";
        Assert.Throws<FormatException>(() => new FrameParser(t));
    }

    [Fact]
    public void TemplateValidate_EmptyHeader_Throws()
    {
        var t = Sample();
        t.Header = "";
        Assert.Throws<FormatException>(() => new FrameParser(t));
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var t = Sample();
        var parser = new FrameParser(t);
        var frames = new List<ParsedFrame>();
        parser.FrameEmitted += frames.Add;
        parser.Feed(new byte[] { 0xAA, 0x55, 0x05 }); // 半包滞留
        parser.Reset();
        parser.Feed(Build(t, 0x01, new byte[] { 0x01 })); // 完整帧
        Assert.Single(frames); // 滞留半包未拼进新帧
        Assert.True(frames[0].Ok);
    }
}
