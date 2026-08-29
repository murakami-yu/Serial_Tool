using System.Text;
using SerialTool.Core.Checksum;

namespace SerialTool.Core.Framing;

/// <summary>解析出的一帧（含错误帧）。</summary>
/// <param name="Ts">时间戳。</param>
/// <param name="Raw">帧原始字节（错误帧为当前候选段）。</param>
/// <param name="Ok">校验/帧尾是否通过。</param>
/// <param name="Error">失败原因（Ok=true 时为空）。</param>
/// <param name="CommandOffset">命令域在 Raw 中的偏移（无命令域为 -1）。</param>
/// <param name="PayloadOffset">数据域在 Raw 中的偏移。</param>
/// <param name="PayloadLength">数据域长度。</param>
public sealed record ParsedFrame(
    DateTime Ts,
    byte[] Raw,
    bool Ok,
    string Error,
    int CommandOffset,
    int PayloadOffset,
    int PayloadLength);

/// <summary>
/// 流式帧解析器：字节流喂入，自动处理粘包/半包/前导杂波/假帧头/校验失败重同步。
/// 缓冲扫描模型：找帧头 → 收齐整帧 → 校验 → 出帧；出错丢弃首个字节重扫
/// （错误帧内部可能嵌有真帧头，逐字节回退保证不丢后续帧）。
/// </summary>
public sealed class FrameParser
{
    private readonly FrameTemplate _t;
    private readonly byte[] _header;
    private readonly byte[] _footer;
    private readonly int _checksumSize;
    private readonly int _fixedPart; // 帧头+长度域+命令域+校验域+帧尾
    private readonly List<byte> _buf = new(1024);

    /// <summary>解析出一帧（成功或失败），解析线程同步触发。</summary>
    public event Action<ParsedFrame>? FrameEmitted;

    /// <summary>未成帧被丢弃的字节数（前导杂波 + 假帧头回退）。</summary>
    public long DroppedBytes { get; private set; }

    public FrameParser(FrameTemplate template)
    {
        template.Validate();
        _t = template;
        _header = ParseHex(template.Header);
        _footer = ParseHex(template.Footer);
        _checksumSize = Checksums.SizeOf(template.ChecksumAlg);
        _fixedPart = _header.Length + template.LengthBytes + template.CmdBytes
                     + _checksumSize + _footer.Length;
    }

    /// <summary>喂入一段原始字节（粘包/半包随意，内部缓冲拼接）。</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        _buf.AddRange(data);
        ParseLoop();
    }

    /// <summary>清空缓冲（切换模板/清屏/重连时）。</summary>
    public void Reset() => _buf.Clear();

    private void ParseLoop()
    {
        while (true)
        {
            // 1. 找帧头（找不到则保留可能成为帧头前缀的尾部字节）
            var idx = IndexOfHeader();
            if (idx < 0)
            {
                var keep = _header.Length - 1;
                if (_buf.Count > keep)
                {
                    DroppedBytes += _buf.Count - keep;
                    _buf.RemoveRange(0, _buf.Count - keep);
                }
                return;
            }
            if (idx > 0)
            {
                DroppedBytes += idx;
                _buf.RemoveRange(0, idx);
            }

            // 2. 长度域（半包则等更多字节）
            int payloadLen;
            if (_t.LengthBytes == 0)
            {
                payloadLen = _t.FixedPayloadLength;
            }
            else
            {
                if (_buf.Count < _header.Length + _t.LengthBytes) return;
                payloadLen = ReadLength();
                if (payloadLen > _t.MaxFrameLength)
                {
                    // 假帧头：回退一个字节重扫（真帧头可能紧跟其后）
                    DropFirstByte();
                    continue;
                }
            }

            // 3. 整帧收齐（半包则等）
            var total = _fixedPart + payloadLen;
            if (_buf.Count < total) return;
            var raw = _buf.GetRange(0, total).ToArray();

            // 4. 帧尾
            if (_footer.Length > 0 && !raw.AsSpan(_fixedPart + payloadLen - _footer.Length, _footer.Length).SequenceEqual(_footer))
            {
                Emit(new ParsedFrame(DateTime.Now, raw, false, "帧尾不匹配", -1, _header.Length + _t.LengthBytes + _t.CmdBytes, payloadLen));
                DropFirstByte();
                continue;
            }

            // 5. 校验域
            if (_checksumSize > 0)
            {
                var calc = Checksums.Compute(_t.ChecksumAlg, raw.AsSpan(0, raw.Length - _checksumSize));
                var actual = raw[^_checksumSize..];
                if (!calc.AsSpan().SequenceEqual(actual))
                {
                    Emit(new ParsedFrame(DateTime.Now, raw, false,
                        $"校验错误(期望 {Hex.Encode(calc)} 实际 {Hex.Encode(actual)})",
                        -1, _header.Length + _t.LengthBytes + _t.CmdBytes, payloadLen));
                    DropFirstByte();
                    continue;
                }
            }

            var cmdOff = _t.CmdBytes > 0 ? _header.Length + _t.LengthBytes : -1;
            var payOff = _header.Length + _t.LengthBytes + _t.CmdBytes;
            Emit(new ParsedFrame(DateTime.Now, raw, true, string.Empty, cmdOff, payOff, payloadLen));
            _buf.RemoveRange(0, total);
        }
    }

    private int IndexOfHeader()
    {
        // 不用 Span.IndexOf(单字节序列) 的多字节重载以保持 .NET 兼容；手写朴素匹配（缓冲量级小）
        for (var i = 0; i + _header.Length <= _buf.Count; i++)
        {
            var hit = true;
            for (var k = 0; k < _header.Length; k++)
            {
                if (_buf[i + k] != _header[k]) { hit = false; break; }
            }
            if (hit) return i;
        }
        return -1;
    }

    private int ReadLength()
    {
        var off = _header.Length;
        return _t.LengthBytes switch
        {
            1 => _buf[off],
            2 => _t.LengthBigEndian
                ? (_buf[off] << 8) | _buf[off + 1]
                : _buf[off] | (_buf[off + 1] << 8),
            _ => _t.FixedPayloadLength,
        };
    }

    private void DropFirstByte()
    {
        DroppedBytes++;
        _buf.RemoveAt(0);
    }

    private void Emit(ParsedFrame f) => FrameEmitted?.Invoke(f);

    private static byte[] ParseHex(string text)
        => Hex.TryParse(text, out var b) ? b : throw new FormatException($"HEX 非法: {text}");
}
