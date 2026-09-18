using System.Windows;
using System.Windows.Controls;
namespace SerialTool.App;

/// <summary>新建 SSH 会话结果（凭据仅内存传递）。</summary>
public sealed record SshConnectRequest(
    string? SaveName, string Host, int Port, string User,
    string? Password, string? KeyPath, string? KeyPassphrase, bool SaveToList);

/// <summary>终端窗新建 SSH 会话对话框（预填主面板 SSH 参数；保存列表不含凭据）。</summary>
public partial class SshConnectDialog : Window
{
    public SshConnectRequest? Request { get; private set; }

    public SshConnectDialog(ViewModels.MainViewModel vm)
    {
        InitializeComponent();
        HostBox.Text = vm.SshHost;
        PortBox.Text = vm.SshPort.ToString();
        UserBox.Text = vm.SshUser;
        AuthBox.SelectedIndex = vm.SshAuthIndex;
        if (vm.SshAuthIndex == 1)
            KeyBox.Text = vm.SshKeyPath;
    }

    /// <summary>按已保存会话预填（快速连接）。</summary>
    public void Prefill(Services.SavedSession s)
    {
        NameBox.Text = s.Name;
        HostBox.Text = s.Host;
        PortBox.Text = s.Port.ToString();
        UserBox.Text = s.User;
        AuthBox.SelectedIndex = s.AuthIndex is 0 or 1 ? s.AuthIndex : 0;
        KeyBox.Text = s.KeyPath ?? "";
    }

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KeyPanel is null) return; // XAML 初始化期
        var isKey = AuthBox.SelectedIndex == 1;
        KeyPanel.Visibility = isKey ? Visibility.Visible : Visibility.Collapsed;
        PasswordBox.Visibility = isKey ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择私钥文件",
            Filter = "私钥文件|id_*;*.pem;*.key;*.openssh|所有文件|*.*",
        };
        if (dlg.ShowDialog(this) == true)
            KeyBox.Text = dlg.FileName;
    }

    private void OnConnect(object sender, RoutedEventArgs e)
    {
        var host = HostBox.Text.Trim();
        var user = UserBox.Text.Trim();
        if (host.Length == 0) { ShowErr("请输入主机地址"); return; }
        if (user.Length == 0) { ShowErr("请输入用户名"); return; }
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1 or > 65535)
        { ShowErr("端口需为 1–65535"); return; }

        var isKey = AuthBox.SelectedIndex == 1;
        string? password = null, keyPath = null, keyPass = null;
        if (isKey)
        {
            keyPath = KeyBox.Text.Trim();
            if (keyPath.Length == 0) { ShowErr("请选择私钥文件"); return; }
            keyPass = KeyPassBox.Password.Length > 0 ? KeyPassBox.Password : null;
        }
        else
        {
            password = PasswordBox.Password.Length > 0 ? PasswordBox.Password : null;
        }

        Request = new SshConnectRequest(
            string.IsNullOrWhiteSpace(NameBox.Text) ? null : NameBox.Text.Trim(),
            host, port, user, password, keyPath, keyPass, SaveBox.IsChecked == true);
        DialogResult = true;
    }

    private void ShowErr(string msg)
    {
        Err.Text = msg;
        Err.Visibility = Visibility.Visible;
    }
}
