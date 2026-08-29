using System.Text.Json;
using SerialTool.Core.Checksum;
using SerialTool.Core.Framing;
using Xunit;

namespace SerialTool.Core.Tests;

public class FrameParserTests
{
    /// <summary>HEX 文本转字节（空串=空数组，非法即抛）。</summary>
    private static byte[] H(string s)
        => SerialTool.Core.Hex.TryParse(s, out var b) && (b.Length > 0 || string.IsNullOrWhiteSpace(s))
            ? b
            : throw new FormatException($"HEX 非法: {s}");

    /// <summary>按模板追加校验（应用模板的字节序配置）。</summary>
    private static byte[] AppendCk(FrameTemplate t, byte[] body)
    {
        var ck = Checksums.Compute(t.ChecksumAlg, body);
        if (t.ChecksumBigEndian != Checksums.WireIsBigEndian(t.ChecksumAlg))
            ck = ck.Reverse().ToArray();
        return body.Concat(ck).ToArray();
    }

    /// <summary>补帧尾。</summary>
    private static byte[] Frame(FrameTemplate t, byte[] body)
        => AppendCk(t, body).Concat(H(t.Footer)).ToArray();

    private static List<ParsedFrame> Run(FrameTemplate t, params byte[][] chunks)
    {
        var parser = new FrameParser(t);
        var frames = new List<ParsedFrame>();
        parser.FrameEmitted += frames.Add;
        foreach (var c in chunks)
            parser.Feed(c);
        return frames;
    }

    private static List<ParsedFrame> RunMulti(List<FrameTemplate> ts, params byte[][] chunks)
    {
        var parser = new MultiFrameParser(ts);
        var frames = new List<ParsedFrame>();
        parser.FrameEmitted += frames.Add;
        foreach (var c in chunks)
            parser.Feed(c);
        return frames;
    }

    // ---------- 用户协议 1：Modbus（长度域在帧中间 + CRC 高前低后） ----------

    [Fact]
    public void Modbus_ReadRequest_010300000001()
    {
        var t = FrameTemplate.Samples()[0]; // 定长：01 03 0000 0001 + CRC(高前)
        var body = H("01 03 00 00 00 01");
        var f = Run(t, Frame(t, body));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(1, f[0].CommandOffset);           // 帧头 1 字节后即命令域
        Assert.Equal(6, f[0].PayloadOffset);           // header1+cmd1+fixed4
        Assert.Equal(0, f[0].PayloadLength);
        Assert.Equal(8, f[0].Raw.Length);              // 6 + CRC2
    }

    [Fact]
    public void Modbus_WriteRequest_LengthInMiddle()
    {
        var t = FrameTemplate.Samples()[1]; // 01 10 [addr2][qty2][len1] data + CRC
        var payload = H("AA 01 52 03 00 01 4E 96 3C 40"); // 10 字节写入数据
        var body = H("01 10 00 00 00 01").Concat(new byte[] { 0x0A }).Concat(payload).ToArray();
        var f = Run(t, Frame(t, body));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(0x10, f[0].Raw[1]);               // cmd 在帧头后
        Assert.Equal(7, f[0].PayloadOffset);           // header1+cmd1+fixed4+len1
        Assert.Equal(10, f[0].PayloadLength);          // byteCount = 0x0A
        Assert.Equal(19, f[0].Raw.Length);             // 17 + CRC2
    }

    [Fact]
    public void Modbus_ReadResponse_LengthField()
    {
        var t = FrameTemplate.Samples()[2]; // 01 03 [len] data + CRC
        var body = H("01 03 02").Concat(H("12 34")).ToArray();
        var f = Run(t, Frame(t, body));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(2, f[0].PayloadLength);
    }

    [Fact]
    public void ChecksumBigEndian_WireOrderApplied()
    {
        // 同一帧体，大端/小端模板互不认账（校验值字节序不同）
        var big = FrameTemplate.Samples()[0];          // ChecksumBigEndian = true
        var little = new FrameTemplate
        {
            Name = "小端版",
            Header = "01",
            Fields = { new FrameField { Kind = "cmd", Size = 1 }, new FrameField { Kind = "fixed", Size = 4 }, new FrameField { Kind = "data", DataFixedSize = 0 } },
            Checksum = "crc16Modbus",
            ChecksumBigEndian = false,                 // Modbus 标准低前
        };
        var frameBig = Frame(big, H("01 03 00 00 00 01"));
        Assert.True(Run(big, frameBig)[0].Ok);
        Assert.False(Run(little, frameBig)[0].Ok);     // 字节序不符 → 校验失败
        var frameLittle = Frame(little, H("01 03 00 00 00 01"));
        Assert.True(Run(little, frameLittle)[0].Ok);
    }

    // ---------- 用户协议 2：EM（无长度域，帧尾扫描） ----------

    [Fact]
    public void EM_ScanToFooter()
    {
        var t = FrameTemplate.Samples()[3]; // EA [src/dst2] [cmd] data CRC ED
        var body = H("EA 01 02 05").Concat(H("11 22 33")).ToArray(); // data = 11 22 33
        var f = Run(t, Frame(t, body));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(3, f[0].CommandOffset);           // EA + 2 字节地址
        Assert.Equal(4, f[0].PayloadOffset);
        Assert.Equal(3, f[0].PayloadLength);
    }

    [Fact]
    public void EM_DataContainsFooterByte_ScanNext()
    {
        var t = FrameTemplate.Samples()[3];
        var payload = H("11 ED 33");                    // 数据域内出现帧尾字节 ED
        var body = H("EA 01 02 05").Concat(payload).ToArray();
        var f = Run(t, Frame(t, body));                 // 正确 CRC 在真帧尾前
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(3, f[0].PayloadLength);            // ED 被正确归入数据域
    }

    // ---------- 多模板仲裁（帧头相同结构不同 / 帧头不同混合流） ----------

    [Fact]
    public void Multi_ModbusReadAndWriteMixed()
    {
        var ts = FrameTemplate.Samples();               // 读请求(定长)在前 = 高优先级
        var read = Frame(ts[0], H("01 03 00 00 00 01"));
        var write = Frame(ts[1],
            H("01 10 00 00 00 02").Concat(new byte[] { 0x04 }).Concat(H("AA BB CC DD")).ToArray());
        var f = RunMulti(ts, read.Concat(write).ToArray());
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.True(x.Ok));
        Assert.Equal(ts[0].Name, f[0].TemplateName);    // 定长模板吃读帧
        Assert.Equal(ts[1].Name, f[1].TemplateName);    // 写帧被读模板结构拒绝（长度不符后 CRC 失败），由写模板吃
        Assert.Equal(4, f[1].PayloadLength);
    }

    [Fact]
    public void Multi_DifferentHeaders_StreamSplit()
    {
        var ts = FrameTemplate.Samples();               // Modbus 家族 + EM
        var em = Frame(ts[3], H("EA 01 02 05").Concat(H("77 88")).ToArray());
        var read = Frame(ts[0], H("01 03 00 00 00 01"));
        var f = RunMulti(ts, em.Concat(read).ToArray());
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.True(x.Ok));
        Assert.Equal(ts[3].Name, f[0].TemplateName);
        Assert.Equal(ts[0].Name, f[1].TemplateName);
    }

    [Fact]
    public void Multi_BadFrameSameFamily_EmitsErrorThenParsesGood()
    {
        var ts = FrameTemplate.Samples();
        var bad = Frame(ts[0], H("01 03 00 00 00 01"));
        bad[^2] ^= 0xFF;                                // 破坏 CRC
        var good = Frame(ts[0], H("01 03 00 00 00 02"));
        var f = RunMulti(ts, bad.Concat(good).ToArray());
        Assert.Equal(2, f.Count);
        Assert.False(f[0].Ok);
        Assert.Contains("校验错误", f[0].Error);
        Assert.True(f[1].Ok);
    }

    [Fact]
    public void Multi_CorruptBytesBeforeForeignGoodFrame_AbsorbedAsGarbage()
    {
        // 跨协议坏字节 + 有效 EM 帧：校验通过的帧优先，坏字节按前导杂波吸收（计入丢弃）
        var ts = FrameTemplate.Samples();
        var bad = Frame(ts[0], H("01 03 00 00 00 01"));
        bad[^2] ^= 0xFF;
        var good = Frame(ts[3], H("EA 01 02 05").Concat(H("99")).ToArray());
        var parser = new MultiFrameParser(ts);
        var f = new List<ParsedFrame>();
        parser.FrameEmitted += f.Add;
        parser.Feed(bad.Concat(good).ToArray());
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(ts[3].Name, f[0].TemplateName);
        Assert.Equal(8, parser.DroppedBytes);           // 坏字节全部计入丢弃
    }

    // ---------- 通用行为（沿用 v1 保证） ----------

    [Fact]
    public void HalfPacket_ByteByByteFeed()
    {
        var t = FrameTemplate.Samples()[2];
        var frame = Frame(t, H("01 03 02").Concat(H("56 78")).ToArray());
        var f = Run(t, frame.Select(b => new[] { b }).ToArray());
        Assert.Single(f);
        Assert.True(f[0].Ok);
    }

    [Fact]
    public void StickyPackets_TwoFramesOneChunk()
    {
        var t = FrameTemplate.Samples()[2];
        var a = Frame(t, H("01 03 02").Concat(H("01 02")).ToArray());
        var b = Frame(t, H("01 03 02").Concat(H("03 04")).ToArray());
        var f = Run(t, a.Concat(b).ToArray());
        Assert.Equal(2, f.Count);
        Assert.All(f, x => Assert.True(x.Ok));
    }

    [Fact]
    public void FalseHeader_HugeLength_Skipped()
    {
        var t = FrameTemplate.Samples()[2];
        t.MaxFrameLength = 32;
        var falseHeader = H("01 03 FF");                // 长度 0xFF 超限 → 假帧头
        var good = Frame(t, H("01 03 02").Concat(H("0A 0B")).ToArray());
        var f = Run(t, falseHeader.Concat(good).ToArray());
        Assert.Single(f);
        Assert.True(f[0].Ok);
    }

    [Fact]
    public void NoChecksum_PassesThrough()
    {
        var t = new FrameTemplate
        {
            Name = "无校验定长",
            Header = "AA",
            Fields = { new FrameField { Kind = "data", DataFixedSize = 3 } },
            Checksum = "none",
        };
        var f = Run(t, H("AA 12 34 56"));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(3, f[0].PayloadLength);
    }

    [Fact]
    public void TwoByteLength_BigEndian()
    {
        var t = new FrameTemplate
        {
            Name = "双字节大端长度",
            Header = "AA 55",
            Fields = { new FrameField { Kind = "length", Size = 2, BigEndian = true }, new FrameField { Kind = "data" } },
            Checksum = "xor",
        };
        var payload = new byte[300];
        for (var i = 0; i < payload.Length; i++) payload[i] = (byte)i;
        var body = H("AA 55").Concat(new byte[] { 0x01, 0x2C }).Concat(payload).ToArray(); // 300 = 0x012C
        var f = Run(t, AppendCk(t, body));
        Assert.Single(f);
        Assert.True(f[0].Ok);
        Assert.Equal(300, f[0].PayloadLength);
    }

    [Fact]
    public void Reset_ClearsBuffer()
    {
        var t = FrameTemplate.Samples()[2];
        var parser = new FrameParser(t);
        var frames = new List<ParsedFrame>();
        parser.FrameEmitted += frames.Add;
        parser.Feed(H("01 03 02 12"));                  // 半包滞留
        parser.Reset();
        parser.Feed(Frame(t, H("01 03 02").Concat(H("34 56")).ToArray()));
        Assert.Single(frames);
        Assert.True(frames[0].Ok);
    }

    // ---------- 模板校验与迁移 ----------

    [Fact]
    public void TemplateValidate_BadHeader_Throws()
    {
        var t = FrameTemplate.Samples()[0];
        t.Header = "GG";
        Assert.Throws<FormatException>(() => new FrameParser(t));
    }

    [Fact]
    public void TemplateValidate_TwoLengthFields_Throws()
    {
        var t = new FrameTemplate
        {
            Header = "AA",
            Fields = { new FrameField { Kind = "length", Size = 1 }, new FrameField { Kind = "length", Size = 1 }, new FrameField { Kind = "data" } },
        };
        Assert.Throws<FormatException>(() => t.Validate());
    }

    [Fact]
    public void TemplateValidate_ScanWithoutFooter_Throws()
    {
        var t = new FrameTemplate
        {
            Header = "AA",
            Fields = { new FrameField { Kind = "data", ScanToFooter = true } },
        };
        Assert.Throws<FormatException>(() => t.Validate());
    }

    [Fact]
    public void MigrateV1_ConvertsToFieldChain()
    {
        var v1 = """
        {
          "name": "旧模板",
          "header": "AA 55",
          "lengthBytes": 1,
          "lengthBigEndian": false,
          "cmdBytes": 1,
          "checksum": "crc16Modbus",
          "maxFrameLength": 256
        }
        """;
        var migrated = FrameTemplate.MigrateV1(JsonDocument.Parse(v1).RootElement);
        Assert.NotNull(migrated);
        Assert.Equal(3, migrated!.Fields.Count);         // cmd(1) + length(1) + data
        Assert.Equal("cmd", migrated.Fields[0].Kind);
        Assert.Equal("length", migrated.Fields[1].Kind);
        Assert.Equal("data", migrated.Fields[2].Kind);
        // v2 JSON 不迁移
        var v2 = JsonSerializer.Serialize(FrameTemplate.Samples()[0]);
        Assert.Null(FrameTemplate.MigrateV1(JsonDocument.Parse(v2).RootElement));
    }
}
