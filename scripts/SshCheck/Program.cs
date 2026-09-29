using System.Text;
using SerialTool.Backends.Ssh;

// Serial_Tool SSH 后端端到端验证：连本机 WSL sshd，跑通 连接/收发/改尺寸/断开 全链路。
// 用法: SshCheck [host] [port] [user] [password]

var positional = args.Where(a => a != "raw").ToArray();
var host = positional.Length > 0 ? positional[0] : "127.0.0.1";
var port = positional.Length > 1 ? int.Parse(positional[1]) : 22;
var user = positional.Length > 2 ? positional[2] : "murakami";
var password = positional.Length > 3 ? positional[3] : "SerialTool@2024";

if (args.Contains("raw"))
    return RawProbe.Run(host, port, user, password);

var rx = new StringBuilder();
var errors = new List<string>();
var failures = new List<string>();

void Check(bool ok, string label)
{
    Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {label}");
    if (!ok) failures.Add(label);
}

var backend = new SshBackend();
backend.HostKeyVerifying += (_, c) =>
{
    Console.WriteLine($"  host-key: {c.Host}:{c.Port} {c.Algorithm} SHA256={c.FingerprintSha256}");
    c.Accepted = true; // 测试环境自动信任（App 内走 TOFU 弹窗 + known_hosts）
};
backend.DataReceived += (_, d) =>
{
    lock (rx) rx.Append(Encoding.UTF8.GetString(d.Bytes));
};
backend.ErrorOccurred += (_, msg) =>
{
    Console.WriteLine($"  error-event: {msg}");
    lock (errors) errors.Add(msg);
};

// ---- 1. 密码认证连接 ----
try
{
    backend.Open(new SshConfig(host, port, user, password, null, null, 100, 30));
    Check(true, "密码认证连接 Open()");
}
catch (Exception ex)
{
    Check(false, $"密码认证连接 Open(): {ex.GetType().Name}: {ex.Message}");
    Console.WriteLine($"RESULT: FAIL ({failures.Count} 项失败)");
    return 1;
}
Check(backend.IsOpen, "IsOpen == true");

// 等 shell 横幅
Thread.Sleep(2000);

// ---- 2. 交互收发 ----
backend.Write(Encoding.UTF8.GetBytes("echo SSH_OK_$((6*7))\n"));
Thread.Sleep(1500);
string snapshot;
lock (rx) snapshot = rx.ToString();
Console.WriteLine($"  raw-rx({snapshot.Length}B): [{snapshot.Replace("\r", "\\r").Replace("\n", "\\n")}]");
Check(snapshot.Contains("SSH_OK_42"), $"交互收发（回显含 SSH_OK_42）");

// ---- 3. 终端改尺寸 ----
try
{
    backend.ResizeTerminal(120, 40);
    Thread.Sleep(300);
    backend.Write(Encoding.UTF8.GetBytes("stty size\n"));
    Thread.Sleep(1500);
    lock (rx) snapshot = rx.ToString();
    Check(snapshot.Contains("40 120"), "ResizeTerminal 生效（stty size = 40 120）");
}
catch (Exception ex)
{
    Check(false, $"ResizeTerminal: {ex.Message}");
}

// ---- 4. Write 大数据（超过 ShellBufferSize=4096，测分片/阻塞） ----
var big = new string('A', 20000);
backend.Write(Encoding.UTF8.GetBytes($"echo MARK_START; printf '{big}\\n' | wc -c\n"));
Thread.Sleep(2000);
lock (rx) snapshot = rx.ToString();
Check(snapshot.Contains("20001"), "大数据写入（printf 20000 字节 → wc -c = 20001）");

// ---- 5. 正常断开 ----
backend.Close();
Check(!backend.IsOpen, "Close() 后 IsOpen == false");
lock (errors) Check(errors.Count == 0, $"无 ErrorOccurred 事件（实际 {errors.Count} 个）");

// ---- 6. 私钥认证（可选：positional[4] = 私钥路径） ----
if (positional.Length > 4)
{
    var keyRx = new StringBuilder();
    var keyBackend = new SshBackend();
    keyBackend.HostKeyVerifying += (_, c) => c.Accepted = true;
    keyBackend.DataReceived += (_, d) => { lock (keyRx) keyRx.Append(Encoding.UTF8.GetString(d.Bytes)); };
    try
    {
        keyBackend.Open(new SshConfig(host, port, user, null, positional[4], null));
        keyBackend.Write(Encoding.UTF8.GetBytes("echo KEY_OK_$((5*5))\n"));
        Thread.Sleep(1500);
        string keySnapshot;
        lock (keyRx) keySnapshot = keyRx.ToString();
        Check(keySnapshot.Contains("KEY_OK_25"), "私钥认证连接 + 收发");
        keyBackend.Close();
    }
    catch (Exception ex)
    {
        Check(false, $"私钥认证: {ex.GetType().Name}: {ex.Message}");
    }
}

// ---- 7. 负路径：错误密码应抛认证异常而不是挂死 ----
var bad = new SshBackend();
bad.HostKeyVerifying += (_, c) => c.Accepted = true;
try
{
    bad.Open(new SshConfig(host, port, user, "wrong-password", null, null));
    Check(false, "错误密码应抛异常");
}
catch (Exception ex)
{
    Check(true, $"错误密码抛异常（{ex.GetType().Name}）");
}

Console.WriteLine(failures.Count == 0 ? "RESULT: ALL PASS" : $"RESULT: FAIL ({failures.Count} 项)");
return failures.Count == 0 ? 0 : 1;
