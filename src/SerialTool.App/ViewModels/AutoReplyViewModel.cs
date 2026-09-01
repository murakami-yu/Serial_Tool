using CommunityToolkit.Mvvm.ComponentModel;
using SerialTool.Core.Framing;

namespace SerialTool.App.ViewModels;

/// <summary>
/// 一条自动应答规则（可编辑）+ 命中计数（运行时统计）。
/// Snapshot 为配置不可变副本：匹配逻辑只读快照，与 UI 编辑解耦；命中计数变化不触发保存。
/// </summary>
public partial class AutoReplyViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _name = "reply1";

    /// <summary>限定来源模板名，空 = 任意模板。</summary>
    [ObservableProperty]
    private string _template = string.Empty;

    /// <summary>命令域前缀（HEX 文本），空 = 不过滤。</summary>
    [ObservableProperty]
    private string _commandHex = string.Empty;

    /// <summary>帧内任意位置子串（HEX 文本），空 = 不过滤。</summary>
    [ObservableProperty]
    private string _matchHex = string.Empty;

    /// <summary>回复帧全文（HEX 文本）。</summary>
    [ObservableProperty]
    private string _replyHex = string.Empty;

    /// <summary>命中后延迟发送毫秒数（调度粒度 50ms）。</summary>
    [ObservableProperty]
    private int _delayMs;

    /// <summary>仅首次命中生效（命中后自动禁用，防握手类指令重复应答）。</summary>
    [ObservableProperty]
    private bool _once;

    /// <summary>命中计数（仅 UI 显示，不持久化）。</summary>
    [ObservableProperty]
    private long _hitCount;

    /// <summary>配置快照：任意配置属性变更后重建（命中计数除外）。</summary>
    public AutoReplyRule Snapshot { get; private set; }

    /// <summary>配置变化通知（MainViewModel 订阅：HEX 合法性提示 + 自动保存）。</summary>
    public event Action<AutoReplyViewModel>? ConfigChanged;

    public AutoReplyViewModel() => Snapshot = BuildSnapshot();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(HitCount)) return; // 运行时统计不算配置变化
        Snapshot = BuildSnapshot();
        ConfigChanged?.Invoke(this);
    }

    private AutoReplyRule BuildSnapshot() => new()
    {
        Name = Name,
        Enabled = Enabled,
        Template = Template,
        CommandHex = CommandHex,
        MatchHex = MatchHex,
        ReplyHex = ReplyHex,
        DelayMs = DelayMs,
        Once = Once,
    };
}
