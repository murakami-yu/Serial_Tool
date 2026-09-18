using Renci.SshNet;
using Renci.SshNet.Common;

namespace SerialTool.Backends.Ssh;

/// <summary>SSH 连接参数。Cols/Rows 为初始终端尺寸（来自终端视图当前网格，未开过终端时 80×24 兜底）。</summary>
public sealed record SshConfig(string Host, int Port, string Username,
    string? Password, string? KeyFilePath, string? KeyPassphrase, int Cols = 80, int Rows = 24);

/// <summary>主机指纹校验挑战（TOFU）：连接过程中同步抛出，UI 决定 <see cref="Accepted"/>。
/// 指纹为 OpenSSH 格式（SHA256:Base64 无填充）。</summary>
public sealed class SshHostKeyChallenge
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required string Algorithm { get; init; }
    public required string FingerprintSha256 { get; init; }
    public required string FingerprintMd5 { get; init; }
    /// <summary>true = 已保存过该主机但指纹不一致（疑似中间人/重装系统）；false = 首次连接。
    /// 由调用方（VM 查已知库后）在弹窗前设置。</summary>
    public bool Changed { get; set; }
    /// <summary>UI 回写：true = 信任并继续（调用方负责写 known_hosts）。</summary>
    public bool Accepted { get; set; }
}

/// <summary>SSH 后端接口。</summary>
public interface ISshBackend : IBusBackend
{
    /// <summary>连接并打开交互 shell 通道（含认证与主机指纹校验）。失败抛异常（含指纹被拒）。</summary>
    void Open(SshConfig cfg);

    /// <summary>主机指纹校验（连接过程中同步抛出，UI 线程弹窗决定）。必须已订阅再 Open。</summary>
    event EventHandler<SshHostKeyChallenge>? HostKeyVerifying;

    /// <summary>终端网格尺寸变化 → 通道窗口变更（远端 stty size 跟随）。</summary>
    void ResizeTerminal(int cols, int rows);
}

/// <summary>
/// SSH 交互 shell 后端（SSH.NET / Renci.SshNet）。
/// 与串口/TCP 后端同构的事件流，收发/终端视图等上层功能复用。
/// 密钥格式支持 OpenSSH/PEM（PuTTY .ppk 不支持，UI 需提示转换）。
/// </summary>
public sealed class SshBackend : ISshBackend
{
    private SshClient? _client;
    private ShellStream? _shell;
    private Thread? _readThread;
    private volatile bool _running;
    private bool _hostKeyRejected;

    private const int ConnectTimeoutMs = 10_000;
    private const int ShellBufferSize = 4096;

    public string Name => "SSH";
    public bool IsOpen => _running;

    public event EventHandler<TimedData>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;
    public event EventHandler<SshHostKeyChallenge>? HostKeyVerifying;

    /// <summary>SSH 无本地设备可枚举。</summary>
    public IReadOnlyList<DeviceInfo> Scan() => Array.Empty<DeviceInfo>();

    public void Open(SshConfig cfg)
    {
        Close();

        var auth = BuildAuth(cfg);
        var info = new ConnectionInfo(cfg.Host, cfg.Port, cfg.Username, auth)
        {
            Timeout = TimeSpan.FromMilliseconds(ConnectTimeoutMs),
        };
        var client = new SshClient(info);
        try
        {
            _hostKeyRejected = false;
            client.HostKeyReceived += OnHostKeyReceived;
            client.Connect();
        }
        catch (Exception ex) when (_hostKeyRejected)
        {
            client.Dispose();
            throw new InvalidOperationException("已拒绝该服务器的主机指纹，连接未建立", ex);
        }
        catch (Exception ex)
        {
            client.Dispose();
            throw; // 认证失败(SshAuthenticationException)/网络超时等原样上抛，UI 展示 Message
        }

        _client = client;
        try
        {
            _shell = client.CreateShellStream("xterm-256color",
                (uint)Math.Max(10, cfg.Cols), (uint)Math.Max(2, cfg.Rows), 0, 0, ShellBufferSize);
        }
        catch (Exception ex)
        {
            client.Dispose();
            _client = null;
            throw new InvalidOperationException($"打开 shell 通道失败: {ex.Message}", ex);
        }

        _running = true;
        _readThread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = $"SshRead:{cfg.Host}",
        };
        _readThread.Start();
    }

    private static AuthenticationMethod BuildAuth(SshConfig cfg)
    {
        if (cfg.KeyFilePath is { Length: > 0 } path)
        {
            try
            {
                var key = new PrivateKeyFile(path, cfg.KeyPassphrase ?? string.Empty);
                return new PrivateKeyAuthenticationMethod(cfg.Username, key);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"加载私钥失败（仅支持 OpenSSH/PEM 格式，PuTTY .ppk 需转换）: {ex.Message}", ex);
            }
        }
        if (cfg.Password is { Length: > 0 } pwd)
            return new PasswordAuthenticationMethod(cfg.Username, pwd);
        throw new InvalidOperationException("未配置认证方式：请填写密码或选择私钥文件");
    }

    private void OnHostKeyReceived(object? sender, HostKeyEventArgs e)
    {
        var challenge = new SshHostKeyChallenge
        {
            Host = _client?.ConnectionInfo.Host ?? "?",
            Port = _client?.ConnectionInfo.Port ?? 22,
            Algorithm = e.HostKeyName,
            FingerprintSha256 = e.FingerPrintSHA256,
            FingerprintMd5 = e.FingerPrintMD5,
        };
        HostKeyVerifying?.Invoke(this, challenge);
        e.CanTrust = challenge.Accepted;
        if (!challenge.Accepted)
            _hostKeyRejected = true;
    }

    public void ResizeTerminal(int cols, int rows)
        => _shell?.ChangeWindowSize((uint)Math.Max(10, cols), (uint)Math.Max(2, rows),
            (uint)(cols * 10), (uint)(rows * 20)); // 像素提示按标称 10×20 字格

    public void Write(ReadOnlySpan<byte> data)
    {
        var shell = _shell;
        if (!_running || shell is null)
            throw new InvalidOperationException("SSH 连接未打开");
        var buf = data.ToArray();
        shell.Write(buf, 0, buf.Length);
    }

    public void Close()
    {
        _running = false;
        var client = _client;
        if (client is null) return;

        try { client.Disconnect(); } catch { /* 关闭异常不影响状态复位 */ }
        try { client.Dispose(); } catch { }
        _shell = null;
        _client = null;
        _readThread?.Join(500);
        _readThread = null;
    }

    public void Dispose() => Close();

    private void ReadLoop()
    {
        var shell = _shell!;
        var buf = new byte[8192];
        while (_running)
        {
            try
            {
                var n = shell.Read(buf, 0, buf.Length);
                if (n <= 0) break; // 通道正常关闭
                var data = new byte[n];
                Array.Copy(buf, data, n);
                DataReceived?.Invoke(this, new TimedData(DateTime.Now, data));
            }
            catch (Exception)
            {
                if (_running)
                    ErrorOccurred?.Invoke(this, "SSH 连接断开（网络中断或服务器关闭）");
                break;
            }
        }
        _running = false;
    }
}
