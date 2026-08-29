using SerialTool.Core.Checksum;
using Xunit;

namespace SerialTool.Core.Tests;

public class ChecksumTests
{
    // 标准测试向量："123456789"（ASCII）
    private static readonly byte[] Check = "123456789"u8.ToArray();

    [Theory]
    [InlineData(ChecksumAlgorithm.Xor, "31")]
    [InlineData(ChecksumAlgorithm.Sum8, "DD")]
    [InlineData(ChecksumAlgorithm.Crc8, "F4")]
    [InlineData(ChecksumAlgorithm.Crc16Modbus, "374B")]   // 0x4B37 小端
    [InlineData(ChecksumAlgorithm.Crc16CcittFalse, "29B1")]
    [InlineData(ChecksumAlgorithm.Crc32, "CBF43926")]
    public void StandardVectors(ChecksumAlgorithm alg, string expectedHex)
    {
        var v = Checksums.Compute(alg, Check);
        Assert.Equal(Hex.ParseHex(expectedHex), v);
    }

    [Theory]
    [InlineData(ChecksumAlgorithm.None, 0)]
    [InlineData(ChecksumAlgorithm.Xor, 1)]
    [InlineData(ChecksumAlgorithm.Sum8, 1)]
    [InlineData(ChecksumAlgorithm.Crc8, 1)]
    [InlineData(ChecksumAlgorithm.Crc16Modbus, 2)]
    [InlineData(ChecksumAlgorithm.Crc16CcittFalse, 2)]
    [InlineData(ChecksumAlgorithm.Crc32, 4)]
    public void SizeOf(ChecksumAlgorithm alg, int size)
        => Assert.Equal(size, Checksums.SizeOf(alg));

    [Fact]
    public void Verify_AcceptsAppendedChecksum()
    {
        foreach (var alg in new[]
                 { ChecksumAlgorithm.Xor, ChecksumAlgorithm.Sum8, ChecksumAlgorithm.Crc8,
                   ChecksumAlgorithm.Crc16Modbus, ChecksumAlgorithm.Crc16CcittFalse, ChecksumAlgorithm.Crc32 })
        {
            var frame = Check.Concat(Checksums.Compute(alg, Check)).ToArray();
            Assert.True(Checksums.Verify(alg, frame), alg.ToString());
        }
    }

    [Fact]
    public void Verify_RejectsCorrupted()
    {
        var frame = Check.Concat(Checksums.Compute(ChecksumAlgorithm.Crc16Modbus, Check)).ToArray();
        frame[^1] ^= 0xFF;
        Assert.False(Checksums.Verify(ChecksumAlgorithm.Crc16Modbus, frame));
    }

    [Fact]
    public void EmptyInput()
    {
        Assert.Equal(new byte[] { 0x00 }, Checksums.Compute(ChecksumAlgorithm.Xor, Array.Empty<byte>()));
        Assert.Equal(new byte[] { 0x00 }, Checksums.Compute(ChecksumAlgorithm.Sum8, Array.Empty<byte>()));
    }
}

file static class Hex
{
    /// <summary>测试辅助：紧凑/带空格 HEX 转字节。</summary>
    public static byte[] ParseHex(string s)
    {
        var clean = s.Replace(" ", "");
        var b = new byte[clean.Length / 2];
        for (var i = 0; i < b.Length; i++)
            b[i] = byte.Parse(clean.Substring(i * 2, 2),
                System.Globalization.NumberStyles.HexNumber);
        return b;
    }
}
