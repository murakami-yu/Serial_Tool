using CommunityToolkit.Mvvm.ComponentModel;
using SerialTool.Core.Framing;

namespace SerialTool.App.ViewModels;

/// <summary>曲线点：会话内时间轴（秒）+ 物理量值。</summary>
public readonly record struct PlotPt(double T, double Y);

/// <summary>
/// 一条字段曲线：可编辑配置 + 运行时点集。
/// 点集由 MainViewModel 的 _plotLock 保护（读线程写 / UI 线程拉快照），不序列化。
/// Snapshot 是配置的不可变副本：读线程求值只读快照，与 UI 编辑天然解耦。
/// </summary>
public partial class FieldPlotViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _enabled = true;

    [ObservableProperty]
    private string _name = "curve1";

    /// <summary>限定来源模板名，空 = 任意模板。</summary>
    [ObservableProperty]
    private string _template = string.Empty;

    /// <summary>命令域前缀过滤（HEX 文本），空 = 不过滤。</summary>
    [ObservableProperty]
    private string _commandHex = string.Empty;

    /// <summary>数据域内字节偏移。</summary>
    [ObservableProperty]
    private int _offset;

    /// <summary>取值宽度：1 / 2 / 4。</summary>
    [ObservableProperty]
    private int _width = 2;

    [ObservableProperty]
    private bool _bigEndian;

    [ObservableProperty]
    private bool _signed;

    /// <summary>物理量换算：y = raw × scale。</summary>
    [ObservableProperty]
    private double _scale = 1.0;

    /// <summary>Y 轴单位标签。</summary>
    [ObservableProperty]
    private string _unit = string.Empty;

    /// <summary>宽度下拉选项（实例属性：DataGrid 行内 ComboBox 绑定）。</summary>
    public int[] WidthChoices { get; } = { 1, 2, 4 };

    /// <summary>运行时点集（解析线程写 / UI 线程读，需 owner 的 _plotLock；配置变更时清空）。</summary>
    public readonly List<PlotPt> Pts = new();

    /// <summary>配置快照：任意属性变更后重建（读线程只读此对象）。</summary>
    public FieldPlotConfig Snapshot { get; private set; }

    /// <summary>配置变化通知（MainViewModel 订阅：清该曲线点集 + 自动保存 + 触发重绘）。</summary>
    public event Action<FieldPlotViewModel>? ConfigChanged;

    public FieldPlotViewModel() => Snapshot = BuildSnapshot();

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        Snapshot = BuildSnapshot(); // 引用原子替换：读线程要么旧快照要么新快照，不撕裂
        ConfigChanged?.Invoke(this);
    }

    private FieldPlotConfig BuildSnapshot() => new()
    {
        Name = Name,
        Enabled = Enabled,
        Template = Template,
        CommandHex = CommandHex,
        Offset = Offset,
        Width = Width,
        BigEndian = BigEndian,
        Signed = Signed,
        Scale = Scale,
        Unit = Unit,
    };
}
