using System.Windows;
using System.Windows.Controls;
using SerialTool.Backends.Rtt;

namespace SerialTool.App;

/// <summary>新建 RTT 会话结果。</summary>
public sealed record RttConnectRequest(
    string? SaveName, string Device, int SpeedKhz, int Iface, int Channel,
    uint? ControlBlockAddress, bool SaveToList);

/// <summary>终端窗新建 RTT 会话对话框（J-Link 探针 + SEGGER RTT 目标）。
/// 探针检测在后台线程跑（首次会触发 J-Link DLL 定位加载，不卡 UI）。无凭据概念。</summary>
public partial class RttConnectDialog : Window
{
    public RttConnectRequest? Request { get; private set; }

    public RttConnectDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => DetectProbes();
    }

    /// <summary>按已保存会话预填（快速连接）。</summary>
    public void Prefill(Services.SavedSession s)
    {
        NameBox.Text = s.Name;
        DeviceBox.Text = s.Host;
        SpeedBox.Text = s.Port.ToString();
        IfaceBox.SelectedIndex = s.AuthIndex is 0 or 1 ? s.AuthIndex : 0;
        if (int.TryParse(s.User, out var ch) && ch is >= 0 and <= 3)
            ChannelBox.SelectedIndex = ch;
        AddrBox.Text = s.KeyPath ?? "";
    }

    /// <summary>后台枚举探针（J-Link DLL 定位 + USB 枚举），回 UI 线程更新状态行。</summary>
    private async void DetectProbes()
    {
        var probes = await Task.Run(() => new RttBackend().Scan());
        var names = string.Join("、", probes.Select(p => p.DisplayName));
        ProbeStatus.Text = probes.Count switch
        {
            > 1 => $"检测到 {probes.Count} 个探针：{names}（连接将使用第一个）",
            1 => $"检测到 {names}",
            _ => "未检测到探针（未装 J-Link 软件或未连接探针，仍可尝试连接）",
        };
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var device = DeviceBox.Text.Trim();
        if (device.Length == 0) { ShowErr("请输入 J-Link 器件名（如 STM32F407VG）"); return; }
        if (!device.All(c => c < 128)) { ShowErr("器件名须为 ASCII"); return; }

        if (!int.TryParse(SpeedBox.Text.Trim(), out var speed) || speed is < 1000 or > 50000)
        { ShowErr("速度需为 1000–50000（kHz）"); return; }

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
            addr, SaveBox.IsChecked == true);
        DialogResult = true;
    }

    private void ShowErr(string msg)
    {
        Err.Text = msg;
        Err.Visibility = Visibility.Visible;
    }
}
