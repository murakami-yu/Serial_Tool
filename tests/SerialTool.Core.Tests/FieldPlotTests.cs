using SerialTool.Core.Framing;
using Xunit;

namespace SerialTool.Core.Tests;

public class FieldPlotTests
{
    /// <summary>构造一帧：帧头 AA 55 + cmd 1B + payload + CRC2B（校验不参与求值，占位即可）。</summary>
    private static ParsedFrame Frame(byte cmd, params byte[] payload)
    {
        var raw = new byte[2 + 1 + payload.Length + 2];
        raw[0] = 0xAA; raw[1] = 0x55; raw[2] = cmd;
        payload.CopyTo(raw, 3);
        return new ParsedFrame(DateTime.Now, raw, Ok: true, Error: "",
            TemplateName: "T1", CommandOffset: 2, PayloadOffset: 3, PayloadLength: payload.Length);
    }

    private static double? Eval(FieldPlotConfig c, ParsedFrame f) => FieldPlotEvaluator.Evaluate(c, f);

    [Fact]
    public void LittleEndian_Width2()
    {
        var f = Frame(0x01, 0x34, 0x12);
        var v = Eval(new FieldPlotConfig { Offset = 0, Width = 2 }, f);
        Assert.Equal(0x1234, v);
    }

    [Fact]
    public void BigEndian_Width2()
    {
        var f = Frame(0x01, 0x12, 0x34);
        var v = Eval(new FieldPlotConfig { Offset = 0, Width = 2, BigEndian = true }, f);
        Assert.Equal(0x1234, v);
    }

    [Theory]
    [InlineData(1, 0xAB, 0xAB)]
    [InlineData(4, 0x01, 1)] // 宽度 4 首字节 01（小端）= 1
    public void Width1And4(int width, byte b0, long expected)
    {
        var f = Frame(0x01, b0, 0x00, 0x00, 0x00);
        var v = Eval(new FieldPlotConfig { Offset = 0, Width = width }, f);
        Assert.Equal(expected, v);
    }

    [Fact]
    public void Signed_Negative()
    {
        var f = Frame(0x01, 0xFF, 0xFF); // -1 (int16 LE)
        var v = Eval(new FieldPlotConfig { Offset = 0, Width = 2, Signed = true }, f);
        Assert.Equal(-1, v);
    }

    [Fact]
    public void Signed_Width1()
    {
        var f = Frame(0x01, 0x80); // -128 (int8)
        var v = Eval(new FieldPlotConfig { Offset = 0, Width = 1, Signed = true }, f);
        Assert.Equal(-128, v);
    }

    [Fact]
    public void Scale()
    {
        var f = Frame(0x01, 0xD0, 0x07); // 2000 LE
        var v = Eval(new FieldPlotConfig { Offset = 0, Width = 2, Scale = 0.1 }, f);
        Assert.Equal(200.0, v);
    }

    [Fact]
    public void OffsetInsidePayload()
    {
        var f = Frame(0x01, 0x00, 0xCD, 0xAB, 0x00);
        var v = Eval(new FieldPlotConfig { Offset = 1, Width = 2 }, f);
        Assert.Equal(0xABCD, v);
    }

    [Theory]
    [InlineData(4, 2)]  // Offset+Width 越出数据域尾部
    [InlineData(0, 8)]  // 宽度非法
    [InlineData(-1, 2)] // 负偏移
    public void OutOfRangeOrNullWidth_ReturnsNull(int offset, int width)
    {
        var f = Frame(0x01, 0x11, 0x22, 0x33); // payload 3 字节
        Assert.Null(Eval(new FieldPlotConfig { Offset = offset, Width = width }, f));
    }

    [Fact]
    public void TemplateMismatch_ReturnsNull()
    {
        var f = Frame(0x01, 0x11, 0x22);
        Assert.Null(Eval(new FieldPlotConfig { Template = "OTHER" }, f));
        Assert.NotNull(Eval(new FieldPlotConfig { Template = "T1" }, f));
        Assert.NotNull(Eval(new FieldPlotConfig { Template = "" }, f)); // 空 = 任意
    }

    [Fact]
    public void CommandPrefixFilter()
    {
        var f = Frame(0x10, 0x11, 0x22); // Raw = AA 55 10 11 22 00 00
        Assert.Null(Eval(new FieldPlotConfig { CommandHex = "03" }, f));
        Assert.NotNull(Eval(new FieldPlotConfig { CommandHex = "10" }, f));
        Assert.NotNull(Eval(new FieldPlotConfig { CommandHex = "10 11" }, f)); // 前缀可跨 cmd/payload 边界
        Assert.Null(Eval(new FieldPlotConfig { CommandHex = "10 11 22 33" }, f)); // 末字节不匹配（CRC 占位 00）
        Assert.Null(Eval(new FieldPlotConfig { CommandHex = "AA 55 10 11 22 33 44" }, f)); // 越出帧长
    }

    [Fact]
    public void Disabled_ReturnsNull()
        => Assert.Null(Eval(new FieldPlotConfig { Enabled = false },
            Frame(0x01, 0x11, 0x22)));

    [Fact]
    public void EmptyPayload_NoSample()
        => Assert.Null(Eval(new FieldPlotConfig { Offset = 0, Width = 1 }, Frame(0x01)));
}
