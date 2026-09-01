using System.Text.Json.Serialization;

namespace SerialTool.Core.Framing;

/// <summary>
/// 自动应答规则：收到匹配的成功帧后，延迟回发指定帧（模拟对端设备）。
/// 匹配条件：模板名（空=任意）+ 命令域前缀（空=忽略）+ 任意子串（空=忽略），三者同时配置取"与"。
/// </summary>
public sealed class AutoReplyRule
{
    [JsonPropertyName("name")] public string Name { get; set; } = "reply1";

    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    /// <summary>限定来源模板名，空 = 任意模板。</summary>
    [JsonPropertyName("template")] public string Template { get; set; } = "";

    /// <summary>命令域前缀（HEX 文本），空 = 不过滤。</summary>
    [JsonPropertyName("commandHex")] public string CommandHex { get; set; } = "";

    /// <summary>帧内任意位置子串（HEX 文本），空 = 不过滤。</summary>
    [JsonPropertyName("matchHex")] public string MatchHex { get; set; } = "";

    /// <summary>回复帧全文（HEX 文本），空或非法 = 规则不生效。</summary>
    [JsonPropertyName("replyHex")] public string ReplyHex { get; set; } = "";

    /// <summary>命中后延迟发送毫秒数（0 = 尽快，粒度由调度器决定）。</summary>
    [JsonPropertyName("delayMs")] public int DelayMs { get; set; }

    /// <summary>仅首次命中生效（命中后由调用方禁用并持久化，适合握手类指令）。</summary>
    [JsonPropertyName("once")] public bool Once { get; set; }
}

/// <summary>规则匹配（纯函数；Once / 命中计数等状态语义由调用方处理）。</summary>
public static class AutoReplyMatcher
{
    /// <summary>第一条启用且匹配的规则，无匹配返回 null。</summary>
    public static AutoReplyRule? Match(IEnumerable<AutoReplyRule> rules, ParsedFrame f)
    {
        foreach (var r in rules)
        {
            if (IsMatch(r, f))
                return r;
        }
        return null;
    }

    /// <summary>
    /// 单条规则是否匹配该帧：启用 + 三个 HEX 字段合法且回复非空 + 模板/命令/子串条件全部满足。
    /// HEX 非法的规则视为不匹配（调用方可另行提示）。
    /// </summary>
    public static bool IsMatch(AutoReplyRule r, ParsedFrame f)
    {
        if (!r.Enabled) return false;
        if (!Hex.TryParse(r.CommandHex, out var cmd)) return false;
        if (!Hex.TryParse(r.MatchHex, out var sub)) return false;
        if (!Hex.TryParse(r.ReplyHex, out var reply) || reply.Length == 0) return false;

        if (r.Template.Length > 0 && !string.Equals(r.Template, f.TemplateName, StringComparison.Ordinal))
            return false;

        if (cmd.Length > 0)
        {
            if (f.CommandOffset < 0 || f.CommandOffset + cmd.Length > f.Raw.Length) return false;
            if (!f.Raw.AsSpan(f.CommandOffset, cmd.Length).SequenceEqual(cmd)) return false;
        }

        if (sub.Length > 0 && !Contains(f.Raw, sub)) return false;

        return true;
    }

    private static bool Contains(byte[] hay, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var hit = true;
            for (var k = 0; k < needle.Length; k++)
            {
                if (hay[i + k] != needle[k]) { hit = false; break; }
            }
            if (hit) return true;
        }
        return false;
    }
}
