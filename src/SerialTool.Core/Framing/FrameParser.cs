using SerialTool.Core.Checksum;

namespace SerialTool.Core.Framing;

/// <summary>解析出的一帧（含错误帧）。</summary>
/// <param name="Ts">时间戳。</param>
/// <param name="Raw">帧原始字节（错误帧为当前候选段）。</param>
/// <param name="Ok">校验/帧尾是否通过。</param>
/// <param name="Error">失败原因（Ok=true 时为空）。</param>
/// <param name="TemplateName">来源模板名（多模板仲裁时区分）。</param>
/// <param name="CommandOffset">命令域在 Raw 中的偏移（无命令域为 -1）。</param>
/// <param name="PayloadOffset">数据域在 Raw 中的偏移。</param>
/// <param name="PayloadLength">数据域长度。</param>
public sealed record ParsedFrame(
    DateTime Ts,
    byte[] Raw,
    bool Ok,
    string Error,
    string TemplateName,
    int CommandOffset,
    int PayloadOffset,
    int PayloadLength);

/// <summary>单模板解析尝试结果。</summary>
internal enum ParseStatus
{
    /// <summary>结构完整（校验可能通过或失败），TotalLen 有效（含帧前杂波）。</summary>
    Frame,

    /// <summary>结构可匹配但字节不足，等待更多数据。</summary>
    NeedMore,

    /// <summary>结构不匹配（无帧头/长度超限），本模板放弃。</summary>
    Mismatch,
}

internal readonly record struct TryResult(
    ParseStatus Status,
    int TotalLen,
    bool CheckOk,
    string Error,
    int CommandOffset,
    int PayloadOffset,
    int PayloadLength,
    int GarbageLen = 0)
{
    internal static readonly TryResult NeedMore = new(ParseStatus.NeedMore, 0, false, "", 0, 0, 0);
    internal static readonly TryResult Mismatch = new(ParseStatus.Mismatch, 0, false, "", 0, 0, 0);
}

/// <summary>
/// 单模板流式解析器：字节流喂入，自动处理粘包/半包/前导杂波/假帧头/校验失败重同步。
/// v2 字段链模型：长度域可在帧中任意位置、数据域支持定长与帧尾扫描、校验字节序可配。
/// </summary>
public sealed class FrameParser
{
    private readonly FrameTemplate _t;
    private readonly byte[] _header;
    private readonly List<byte> _buf = new(1024);

    /// <summary>解析出一帧（成功或失败），解析线程同步触发。</summary>
    public event Action<ParsedFrame>? FrameEmitted;

    /// <summary>未成帧被丢弃的字节数。</summary>
    public long DroppedBytes { get; private set; }

    public FrameParser(FrameTemplate template)
    {
        template.Validate();
        _t = template;
        _header = ParseHex(template.Header);
    }

    /// <summary>喂入一段原始字节（粘包/半包随意，内部缓冲拼接）。</summary>
    public void Feed(ReadOnlySpan<byte> data)
    {
        _buf.AddRange(data);
        while (true)
        {
            var r = TryParse(_t, _buf, out var raw);
            switch (r.Status)
            {
                case ParseStatus.Frame:
                    FrameEmitted?.Invoke(new ParsedFrame(DateTime.Now, raw, r.CheckOk, r.Error,
                        _t.Name, r.CommandOffset, r.PayloadOffset, r.PayloadLength));
                    _buf.RemoveRange(0, r.TotalLen);
                    break;

                case ParseStatus.NeedMore:
                    // 帧头之前的杂波对本模板必然无用，安全裁剪
                    var idx = Find(_buf, _header);
                    if (idx > 0)
                    {
                        DroppedBytes += idx;
                        _buf.RemoveRange(0, idx);
                    }
                    return;

                default: // Mismatch
                    idx = Find(_buf, _header);
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
                    // 帧头在 0 但结构 Mismatch（长度超限等）→ 假帧头回退 1 字节
                    DroppedBytes++;
                    _buf.RemoveAt(0);
                    break;
            }
        }
    }

    /// <summary>清空缓冲。</summary>
    public void Reset() => _buf.Clear();

    // ---------- 核心结构解析（纯函数：不修改缓冲，多模板仲裁共用） ----------

    /// <summary>
    /// 对缓冲按模板组帧尝试（纯函数）。
    /// 帧头可不在缓冲 0 位：TotalLen = 帧前杂波 + 帧长，raw 从帧头截取。
    /// Frame=结构完整（CheckOk 指示校验）；NeedMore=数据不足；Mismatch=本模板放弃。
    /// </summary>
    internal static TryResult TryParse(FrameTemplate t, List<byte> buf, out byte[] raw)
    {
        raw = Array.Empty<byte>();
        var header = ParseHex(t.Header);
        var footer = ParseHex(t.Footer);
        var ckSize = Checksums.SizeOf(t.ChecksumAlg);

        var start = Find(buf, header);
        if (start < 0) return TryResult.Mismatch;

        var offset = start + header.Length;
        var cmdOffset = -1;
        var payOffset = -1;
        var payLen = -1;
        var hasLength = false;

        foreach (var f in t.Fields)
        {
            switch (f.Kind)
            {
                case "cmd":
                    if (buf.Count < offset + f.Size) return TryResult.NeedMore;
                    if (cmdOffset < 0) cmdOffset = offset - start;
                    offset += f.Size;
                    break;

                case "fixed":
                    if (buf.Count < offset + f.Size) return TryResult.NeedMore;
                    offset += f.Size;
                    break;

                case "length":
                {
                    if (buf.Count < offset + f.Size) return TryResult.NeedMore;
                    var v = 0;
                    for (var i = 0; i < f.Size; i++)
                        v = f.BigEndian ? (v << 8) | buf[offset + i] : v | (buf[offset + i] << (8 * i));
                    if (v > t.MaxFrameLength) return TryResult.Mismatch; // 假帧头
                    payLen = v;
                    hasLength = true;
                    offset += f.Size;
                    break;
                }

                case "data" when f.ScanToFooter:
                    return TryScanToFooter(t, buf, start, offset, footer, ckSize, cmdOffset);

                case "data":
                    payLen = hasLength ? payLen : f.DataFixedSize;
                    payOffset = offset - start;
                    if (payLen > t.MaxFrameLength) return TryResult.Mismatch;
                    offset += payLen;
                    break;
            }
        }

        if (payOffset < 0)
        {
            payOffset = offset - start; // 无数据域：空 payload
            payLen = 0;
        }

        var totalRel = offset - start + ckSize + footer.Length;
        if (totalRel > t.MaxFrameLength) return TryResult.Mismatch;
        if (buf.Count < start + totalRel) return TryResult.NeedMore;

        raw = buf.GetRange(start, totalRel).ToArray();
        var r = Verify(t, raw, footer, ckSize, cmdOffset, payOffset, payLen);
        return r with { TotalLen = start + totalRel, GarbageLen = start };
    }

    /// <summary>帧尾扫描模式：数据域长度由帧尾位置界定。帧尾可能出现在数据内 → 逐位置校验。</summary>
    private static TryResult TryScanToFooter(FrameTemplate t, List<byte> buf,
        int start, int dataStart, byte[] footer, int ckSize, int cmdOffset)
    {
        var searchFrom = dataStart;
        while (true)
        {
            var fPos = Find(buf, footer, searchFrom);
            if (fPos < 0) return TryResult.NeedMore;
            var ckStart = fPos - ckSize;
            if (ckStart < dataStart)
            {
                searchFrom = fPos + 1; // 帧尾出现太早，继续找
                continue;
            }
            var totalRel = fPos + footer.Length - start;
            if (totalRel > t.MaxFrameLength) return TryResult.Mismatch;
            if (buf.Count < fPos + footer.Length) return TryResult.NeedMore;

            var raw = buf.GetRange(start, totalRel).ToArray();
            var r = Verify(t, raw, footer, ckSize, cmdOffset, dataStart - start, ckStart - dataStart);
            if (r.CheckOk)
                return r with { TotalLen = start + totalRel, GarbageLen = start };
            searchFrom = fPos + 1; // 数据域内出现帧尾字节 → 尝试下一位置
        }
    }

    /// <summary>帧尾与校验域校验（TotalLen 由调用方填充）。</summary>
    private static TryResult Verify(FrameTemplate t, byte[] raw, byte[] footer,
        int ckSize, int cmdOffset, int payOffset, int payLen)
    {
        if (footer.Length > 0 && !raw.AsSpan(raw.Length - footer.Length).SequenceEqual(footer))
            return new TryResult(ParseStatus.Frame, 0, false, "帧尾不匹配", cmdOffset, payOffset, payLen);

        if (ckSize > 0)
        {
            var calc = Checksums.Compute(t.ChecksumAlg, raw.AsSpan(0, raw.Length - ckSize - footer.Length));
            if (t.ChecksumBigEndian != Checksums.WireIsBigEndian(t.ChecksumAlg))
                calc = calc.Reverse().ToArray();
            var actual = raw.AsSpan(raw.Length - ckSize - footer.Length, ckSize);
            if (!calc.AsSpan().SequenceEqual(actual))
                return new TryResult(ParseStatus.Frame, 0, false,
                    $"校验错误(期望 {Hex.Encode(calc)} 实际 {Hex.Encode(actual.ToArray())})",
                    cmdOffset, payOffset, payLen);
        }
        return new TryResult(ParseStatus.Frame, 0, true, string.Empty, cmdOffset, payOffset, payLen);
    }

    internal static int Find(List<byte> buf, byte[] pattern, int from = 0)
    {
        for (var i = from; i + pattern.Length <= buf.Count; i++)
        {
            var hit = true;
            for (var k = 0; k < pattern.Length; k++)
            {
                if (buf[i + k] != pattern[k]) { hit = false; break; }
            }
            if (hit) return i;
        }
        return -1;
    }

    private static byte[] ParseHex(string text)
        => Hex.TryParse(text, out var b) ? b : throw new FormatException($"HEX 非法: {text}");
}

/// <summary>
/// 多模板并行解析器：同一字节流按模板优先级（列表顺序）仲裁。
/// 策略：先找"结构匹配且校验通过"的最高优先级帧；全部结构匹配但校验失败时，
/// 按优先级输出第一个为错误帧（真坏帧不被其他模板误吞）。
/// 帧头不同的模板天然分流；帧头相同靠长度结构 + 校验区分（如 Modbus 读/写）。
/// </summary>
public sealed class MultiFrameParser
{
    private readonly List<FrameTemplate> _templates;
    private readonly List<byte> _buf = new(1024);
    private const int HoldLimit = 4096; // NeedMore 滞留上限（防假数据无限堆积）

    public event Action<ParsedFrame>? FrameEmitted;

    public long DroppedBytes { get; private set; }

    public MultiFrameParser(IEnumerable<FrameTemplate> templates)
    {
        _templates = templates.ToList();
        foreach (var t in _templates) t.Validate();
        if (_templates.Count == 0)
            throw new ArgumentException("至少需要一个模板", nameof(templates));
    }

    public void Feed(ReadOnlySpan<byte> data)
    {
        _buf.AddRange(data);
        ParseLoop();
    }

    public void Reset() => _buf.Clear();

    private void ParseLoop()
    {
        while (_buf.Count > 0)
        {
            TryResult? bestError = null;
            FrameTemplate? bestErrorTemplate = null;
            byte[] bestErrorRaw = Array.Empty<byte>();
            var waiting = false;

            foreach (var t in _templates)
            {
                var r = FrameParser.TryParse(t, _buf, out var raw);
                if (r.Status == ParseStatus.NeedMore) waiting = true;
                if (r.Status != ParseStatus.Frame) continue;
                if (r.CheckOk)
                {
                    // 校验通过：直接成帧（帧前杂波一并消费并计入丢弃）
                    DroppedBytes += r.GarbageLen;
                    FrameEmitted?.Invoke(new ParsedFrame(DateTime.Now, raw, true, string.Empty,
                        t.Name, r.CommandOffset, r.PayloadOffset, r.PayloadLength));
                    _buf.RemoveRange(0, r.TotalLen);
                    goto NEXT;
                }
                if (bestError is null)
                {
                    bestError = r;
                    bestErrorTemplate = t;
                    bestErrorRaw = raw;
                }
            }

            if (bestError is { } err)
            {
                // 错误帧：整体消费该候选段（1 字节回退会在定长模板下对每个内嵌帧头级联误报）
                DroppedBytes += err.GarbageLen;
                FrameEmitted?.Invoke(new ParsedFrame(DateTime.Now, bestErrorRaw, false, err.Error,
                    bestErrorTemplate!.Name, err.CommandOffset, err.PayloadOffset, err.PayloadLength));
                _buf.RemoveRange(0, err.TotalLen);
                goto NEXT;
            }

            if (waiting)
            {
                // 等更多数据；假帧头导致的无限滞留兜底裁剪
                if (_buf.Count > HoldLimit)
                {
                    DroppedBytes++;
                    _buf.RemoveAt(0);
                    goto NEXT;
                }
                return;
            }

            // 全部 Mismatch：无任何模板帧头 → 保留最短帧头前缀；有帧头但结构不符 → 回退 1 字节
            var anyHeader = _templates.Any(t =>
            {
                var h = Hex.TryParse(t.Header, out var x) ? x : Array.Empty<byte>();
                return h.Length > 0 && FrameParser.Find(_buf, h) >= 0;
            });
            if (!anyHeader)
            {
                var keep = _templates.Min(t =>
                {
                    var h = Hex.TryParse(t.Header, out var x) ? x : Array.Empty<byte>();
                    return Math.Max(0, h.Length - 1);
                });
                if (_buf.Count > keep)
                {
                    DroppedBytes += _buf.Count - keep;
                    _buf.RemoveRange(0, _buf.Count - keep);
                }
                return;
            }
            DroppedBytes++;
            _buf.RemoveAt(0);
        NEXT: ;
        }
    }
}
