using System.Windows;
using System.Windows.Controls;
using SerialTool.Backends.Rtt;

namespace SerialTool.App;

/// <summary>新建 RTT 会话结果。</summary>
public sealed record RttConnectRequest(
    string? SaveName, string Device, int SpeedKhz, int Iface, int Channel,
    uint? ControlBlockAddress, bool SaveToList, uint? SerialNumber = null, bool ResetTarget = true);

/// <summary>终端窗新建 RTT 会话对话框（J-Link 探针 + SEGGER RTT 目标）。
/// 探针检测在后台线程跑（首次会触发 J-Link DLL 定位加载，不卡 UI），检测结果落 AppLog。无凭据概念。</summary>
public partial class RttConnectDialog : Window
{
    public RttConnectRequest? Request { get; private set; }

    public RttConnectDialog()
    {
        InitializeComponent();
        // 默认值 + 上次使用记忆（RTT-T config.json 同款「自动选择」）：代码显式赋值——
        // IsEditable ComboBox 依赖 XAML IsSelected/Text 会在部分系统清空编辑框（真机截图实证），代码赋值才可靠
        var last = Services.RttLastUsedStore.Load();
        IfaceBox.SelectedIndex = last?.Iface is 0 or 1 ? last.Iface : 0;
        SpeedBox.Text = (last?.SpeedKhz ?? 4000).ToString();
        DeviceBox.Text = string.IsNullOrWhiteSpace(last?.Device) ? "STM32F103C8" : last.Device; // 无记忆默认首项（RTT-T 同款）
        ChannelBox.SelectedIndex = last is { Channel: >= 0 and <= 3 } lu ? lu.Channel : 0;
        ResetBox.IsChecked = last?.Reset ?? true;
        Loaded += (_, _) => DetectProbes();
    }

    /// <summary>按已保存会话预填（快速连接）。Notes 编码 RTT 附加参数："sn=xxx;reset=1"。</summary>
    public void Prefill(Services.SavedSession s)
    {
        NameBox.Text = s.Name;
        DeviceBox.Text = s.Host;
        SpeedBox.Text = s.Port.ToString();
        IfaceBox.SelectedIndex = s.AuthIndex is 0 or 1 ? s.AuthIndex : 0;
        if (int.TryParse(s.User, out var ch) && ch is >= 0 and <= 3)
            ChannelBox.SelectedIndex = ch;
        AddrBox.Text = s.KeyPath ?? "";
        foreach (var kv in (s.Notes ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i <= 0) continue;
            var (k, v) = (kv[..i].Trim(), kv[(i + 1)..].Trim());
            if (k == "sn") SnBox.Text = v;
            else if (k == "reset") ResetBox.IsChecked = v != "0";
        }
    }

    /// <summary>后台枚举探针（J-Link DLL 定位 + USB 枚举），回 UI 线程更新状态行并预填 S/N。
    /// DLL 定位失败的原因也落 AppLog——现场日志能看出是「没装软件」还是「没插探针」。</summary>
    private async void DetectProbes()
    {
        var probes = await Task.Run(() => new RttBackend().Scan());
        if (probes.Count == 1)
            SnBox.Text = probes[0].Id; // 单探针自动填 S/N
        var names = string.Join("、", probes.Select(p => p.DisplayName));
        Services.AppLog.Info(probes.Count > 0
            ? $"RTT 对话框：检测到 {probes.Count} 个 J-Link 探针（{names}）"
            : "RTT 对话框：未检测到探针（未装 J-Link 软件或未连接探针）");
        ProbeStatus.Text = probes.Count switch
        {
            > 1 => $"检测到 {probes.Count} 个探针：{names}（连接将使用 S/N 框指定的探针）",
            1 => $"检测到 {names}",
            _ => "未检测到探针（未装 J-Link 软件或未连接探针，仍可尝试连接）",
        };
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var device = DeviceBox.Text.Trim();
        if (device.Length == 0) { ShowErr("请输入 J-Link 器件名（如 STM32F103C8）"); return; }
        if (!device.All(c => c < 128)) { ShowErr("器件名须为 ASCII"); return; }

        if (!int.TryParse(SpeedBox.Text.Trim(), out var speed) || speed is < 1000 or > 50000)
        { ShowErr("速度需为 1000–50000（kHz）"); return; }

        uint? sn = null;
        var snText = SnBox.Text.Trim();
        if (snText.Length > 0)
        {
            if (!uint.TryParse(snText, out var v))
            { ShowErr("探针 S/N 须为数字（留空 = 默认 USB 探针）"); return; }
            sn = v;
        }

        uint? addr = null;
        var addrText = AddrBox.Text.Trim();
        if (addrText.Length > 0)
        {
            var hex = addrText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? addrText[2..] : addrText;
            if (!uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var a))
            { ShowErr("控制块地址须为十六进制（如 0x20000000），留空则自动搜索"); return; }
            addr = a;
        }

        Request = new RttConnectRequest(
            string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim(),
            device, speed, IfaceBox.SelectedIndex, ChannelBox.SelectedIndex,
            addr, SaveBox.IsChecked == true, sn, ResetBox.IsChecked != false);
        // 记忆本次参数（RTT-T config.json 同款），下次打开自动回填
        Services.RttLastUsedStore.Save(
            new Services.RttLastUsed(device, speed, IfaceBox.SelectedIndex, ChannelBox.SelectedIndex,
                ResetBox.IsChecked != false));
        DialogResult = true;
    }

    private void ShowErr(string msg)
    {
        Err.Text = msg;
        Err.Visibility = Visibility.Visible;
    }
}
