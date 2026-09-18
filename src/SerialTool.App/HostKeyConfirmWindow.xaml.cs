using System.Windows;
using SerialTool.Backends.Ssh;
namespace SerialTool.App;

/// <summary>SSH 主机指纹确认对话框（TOFU 首次连接 / 指纹变更警告）。
/// 连接流程中同步 ShowDialog，关闭即回写 <see cref="SshHostKeyChallenge.Accepted"/>。</summary>
public partial class HostKeyConfirmWindow : Window
{
    private readonly SshHostKeyChallenge _challenge;

    public HostKeyConfirmWindow(SshHostKeyChallenge challenge)
    {
        InitializeComponent();
        _challenge = challenge;
        DataContext = challenge;

        HostText.Text = $"{challenge.Host}:{challenge.Port}";
        AlgoText.Text = $"{challenge.Algorithm}";
        FpText.Text = challenge.FingerprintSha256;
        Md5Text.Text = $"MD5: {challenge.FingerprintMd5}";

        if (challenge.Changed)
        {
            Headline.Text = "警告：服务器指纹已变化";
            Warn.Visibility = Visibility.Visible;
            Warn.Text = "该主机的指纹与已保存记录不一致！可能是服务器重装/更换密钥，也可能是中间人攻击。"
                       + "请确认网络环境可信后再决定是否继续。";
        }
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        _challenge.Accepted = true;
        DialogResult = true;
    }

    private void OnReject(object sender, RoutedEventArgs e)
    {
        _challenge.Accepted = false;
        DialogResult = false;
    }
}
