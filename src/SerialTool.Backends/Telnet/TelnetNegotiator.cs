namespace SerialTool.Backends.Telnet;

/// <summary>
/// Telnet IAC 协商器（RFC 854/855），哑终端 + 服务器回显策略：
/// 接受对端 WILL ECHO(1) / WILL SUPPRESS-GO-AHEAD(3)（回 DO，让服务器负责回显与字符模式——
/// 嵌入式 telnetd 的常规预期），其余 WILL 回 DONT；对端 DO 一律回 WONT（不做 NAWS/TTYPE 等）；
/// 子协商（SB…SE）解析后丢弃；IAC IAC 解为数据 0xFF；NOP/GA 等单字节命令忽略。
/// 有状态：IAC 序列跨包分片安全。纯逻辑可单测。
/// </summary>
public sealed class TelnetNegotiator
{
    private const byte IAC = 255, DONT = 254, DO = 253, WONT = 252, WILL = 251, SB = 250, SE = 240;
    private const byte OptEcho = 1, OptSuppressGoAhead = 3;

    private enum State { Data, Iac, Negotiate, Sub, SubIac }

    private State _state = State.Data;
    private byte _negCmd;

    /// <summary>喂入对端字节流：返回 (净数据, 需回写给对端的协商应答字节)。</summary>
    public (byte[] Data, byte[] Replies) Feed(ReadOnlySpan<byte> chunk)
    {
        var data = new List<byte>(chunk.Length);
        var replies = new List<byte>();

        foreach (var b in chunk)
        {
            switch (_state)
            {
                case State.Data:
                    if (b == IAC) _state = State.Iac;
                    else data.Add(b);
                    break;

                case State.Iac:
                    switch (b)
                    {
                        case IAC: // IAC IAC = 数据 0xFF
                            data.Add(IAC);
                            _state = State.Data;
                            break;
                        case WILL or WONT or DO or DONT:
                            _negCmd = b;
                            _state = State.Negotiate;
                            break;
                        case SB:
                            _state = State.Sub;
                            break;
                        default: // NOP(241)/GA(249)/DM/BRK/AYT 等：忽略
                            _state = State.Data;
                            break;
                    }
                    break;

                case State.Negotiate:
                    if (_negCmd == WILL)
                    {
                        var reply = b is OptEcho or OptSuppressGoAhead ? DO : DONT;
                        replies.Add(IAC);
                        replies.Add(reply);
                        replies.Add(b);
                    }
                    else if (_negCmd == DO)
                    {
                        // 我们不提供任何客户端选项（含 NAWS：尺寸不上报，v1 取舍）
                        replies.Add(IAC);
                        replies.Add(WONT);
                        replies.Add(b);
                    }
                    // WONT/DONT：对端撤销能力，无需应答
                    _state = State.Data;
                    break;

                case State.Sub:
                    if (b == IAC) _state = State.SubIac;
                    break;

                case State.SubIac:
                    // IAC SE = 子协商结束；IAC IAC = 子协商内转义 0xFF（一并丢弃）
                    _state = b == SE ? State.Data : State.Sub;
                    break;
            }
        }

        return (data.ToArray(), replies.ToArray());
    }

    /// <summary>本端 → 对端输出转义：数据 0xFF 写作 IAC IAC。</summary>
    public static byte[] Escape(ReadOnlySpan<byte> data)
    {
        var count = 0;
        foreach (var b in data)
            if (b == IAC) count++;
        if (count == 0) return data.ToArray();

        var result = new byte[data.Length + count];
        var i = 0;
        foreach (var b in data)
        {
            if (b == IAC) result[i++] = IAC;
            result[i++] = b;
        }
        return result;
    }
}
