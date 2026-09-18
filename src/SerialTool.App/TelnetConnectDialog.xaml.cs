using System.Windows;
namespace SerialTool.App;

/// <summary>新建 Telnet 会话结果。</summary>
public sealed record TelnetConnectRequest(string? SaveName, string Host, int Port, bool SaveToList);

public partial class TelnetConnectDialog : Window
{
    public TelnetConnectRequest? Request { get; private set; }

    public TelnetConnectDialog()
    {
        InitializeComponent();
        PortBox.Text = "23";
    }

    public void Prefill(Services.SavedSession s)
    {
        NameBox.Text = s.Name;
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString();
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        if (host.Length == 0) { Err.Text = "请输入主机地址"; Err.Visibility = Visibility.Visible; return; }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        { Err.Text = "端口需为 1–65535"; Err.Visibility = Visibility.Visible; return; }

        Request = new TelnetConnectRequest(
            string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim(),
            host, port, SaveBox.IsChecked == true);
        DialogResult = true;
    }
}
