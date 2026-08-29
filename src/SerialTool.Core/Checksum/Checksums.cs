namespace SerialTool.Core.Checksum;

/// <summary>支持的校验算法。</summary>
public enum ChecksumAlgorithm
{
    None,
    Xor,
    Sum8,
    Crc8,
    Crc16Modbus,
    Crc16CcittFalse,
    Crc32,
}

/// <summary>
/// 校验算法库（查表法 CRC，高速率友好）。
/// 标准测试向量（"123456789"）：
/// Xor=0x31  Sum8=0xDD  Crc8=0xF4  Crc16Modbus=0x4B37  Crc16CcittFalse=0x29B1  Crc32=0xCBF43926
/// </summary>
public static class Checksums
{
    /// <summary>校验值字节长度（线上传输宽度）。</summary>
    public static int SizeOf(ChecksumAlgorithm a) => a switch
    {
        ChecksumAlgorithm.None => 0,
        ChecksumAlgorithm.Crc16Modbus or ChecksumAlgorithm.Crc16CcittFalse => 2,
        ChecksumAlgorithm.Crc32 => 4,
        _ => 1,
    };

    /// <summary>计算校验值，返回线上字节序（Modbus 小端，CCITT/CRC32 大端）。</summary>
    public static byte[] Compute(ChecksumAlgorithm a, ReadOnlySpan<byte> data) => a switch
    {
        ChecksumAlgorithm.None => Array.Empty<byte>(),
        ChecksumAlgorithm.Xor => new[] { Xor8(data) },
        ChecksumAlgorithm.Sum8 => new[] { Sum8(data) },
        ChecksumAlgorithm.Crc8 => new[] { Crc8(data) },
        ChecksumAlgorithm.Crc16Modbus => LittleEndian(Crc16Modbus(data)),
        ChecksumAlgorithm.Crc16CcittFalse => BigEndian(Crc16CcittFalse(data), 2),
        ChecksumAlgorithm.Crc32 => BigEndian(Crc32(data), 4),
        _ => throw new ArgumentOutOfRangeException(nameof(a)),
    };

    /// <summary>校验帧尾：frame 末尾 Size 字节与前面内容的计算值比对。</summary>
    public static bool Verify(ChecksumAlgorithm a, ReadOnlySpan<byte> frame)
    {
        var size = SizeOf(a);
        if (size == 0) return true;
        var expect = Compute(a, frame[..^size]);
        return frame[^size..].SequenceEqual(expect);
    }

    /// <summary>算法默认线上字节序是否大端（Modbus 小端，CCITT/CRC32 大端；单字节算法无字节序）。</summary>
    public static bool WireIsBigEndian(ChecksumAlgorithm a) => a switch
    {
        ChecksumAlgorithm.Crc16Modbus => false,
        _ => true,
    };

    // ---------- 基础算法 ----------

    private static byte Xor8(ReadOnlySpan<byte> d)
    {
        byte v = 0;
        foreach (var b in d) v ^= b;
        return v;
    }

    private static byte Sum8(ReadOnlySpan<byte> d)
    {
        byte v = 0;
        foreach (var b in d) v += b;
        return v;
    }

    // ---------- CRC（查表法） ----------

    private static readonly byte[] Crc8Table = BuildCrc8Table();
    private static readonly ushort[] Crc16ModbusTable = BuildReflectedTable(0xA001);
    private static readonly ushort[] Crc16CcittTable = BuildNormalTable16(0x1021);
    private static readonly uint[] Crc32Table = BuildReflectedTable32(0xEDB88320);

    private static byte Crc8(ReadOnlySpan<byte> d)
    {
        byte crc = 0x00; // CRC-8/SMBUS: poly 0x07, init 0x00
        foreach (var b in d)
            crc = (byte)(Crc8Table[crc ^ b]);
        return crc;
    }

    private static ushort Crc16Modbus(ReadOnlySpan<byte> d)
    {
        ushort crc = 0xFFFF; // MODBUS: 反射 0x8005，init/xorout 0xFFFF
        foreach (var b in d)
            crc = (ushort)((crc >> 8) ^ Crc16ModbusTable[(crc ^ b) & 0xFF]);
        return crc;
    }

    private static ushort Crc16CcittFalse(ReadOnlySpan<byte> d)
    {
        ushort crc = 0xFFFF; // CCITT-FALSE: poly 0x1021，init 0xFFFF
        foreach (var b in d)
            crc = (ushort)((crc << 8) ^ Crc16CcittTable[(crc >> 8) ^ b]);
        return crc;
    }

    private static uint Crc32(ReadOnlySpan<byte> d)
    {
        uint crc = 0xFFFFFFFF; // CRC-32: 反射 0x04C11DB7，init/xorout 0xFFFFFFFF
        foreach (var b in d)
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        return ~crc;
    }

    private static byte[] BuildCrc8Table()
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            byte c = (byte)i;
            for (var k = 0; k < 8; k++)
                c = (byte)((c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1);
            t[i] = c;
        }
        return t;
    }

    private static ushort[] BuildReflectedTable(ushort poly)
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            ushort c = (ushort)i;
            for (var k = 0; k < 8; k++)
                c = (ushort)((c & 1) != 0 ? (c >> 1) ^ poly : c >> 1);
            t[i] = c;
        }
        return t;
    }

    private static ushort[] BuildNormalTable16(ushort poly)
    {
        var t = new ushort[256];
        for (var i = 0; i < 256; i++)
        {
            ushort c = (ushort)(i << 8);
            for (var k = 0; k < 8; k++)
                c = (ushort)((c & 0x8000) != 0 ? (c << 1) ^ poly : c << 1);
            t[i] = c;
        }
        return t;
    }

    private static uint[] BuildReflectedTable32(uint poly)
    {
        var t = new uint[256];
        for (var i = 0; i < 256; i++)
        {
            uint c = (uint)i;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (c >> 1) ^ poly : c >> 1;
            t[i] = c;
        }
        return t;
    }

    private static byte[] LittleEndian(ushort v) => new[] { (byte)(v & 0xFF), (byte)(v >> 8) };

    private static byte[] BigEndian(uint v, int size) => size switch
    {
        2 => new[] { (byte)(v >> 8), (byte)(v & 0xFF) },
        4 => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v },
        _ => throw new ArgumentException(null, nameof(size)),
    };
}
