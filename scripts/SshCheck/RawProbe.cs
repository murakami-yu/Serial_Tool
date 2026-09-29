// 直接用 SSH.NET 探针：隔离 SshBackend 与 SSH.NET 库本身的行为差异。
// dotnet run ... -- raw
using System.Text;
using Renci.SshNet;

static class RawProbe
{
    public static int Run(string host, int port, string user, string password)
    {
        var info = new ConnectionInfo(host, port, user,
            new PasswordAuthenticationMethod(user, password));
        using var client = new SshClient(info);
        client.HostKeyReceived += (_, e) => e.CanTrust = true;
        client.Connect();
        Console.WriteLine("raw: connected");

        using var shell = client.CreateShellStream("xterm-256color", 100, 30, 0, 0, 4096);
        var cts = new CancellationTokenSource();
        var t = new Thread(() =>
        {
            var buf = new byte[8192];
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var n = shell.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    var s = Encoding.UTF8.GetString(buf, 0, n);
                    Console.WriteLine($"<<RX {n}B: {s.Replace("\r", "\\r").Replace("\n", "\\n")}>>");
                }
                catch (Exception ex) { Console.WriteLine($"raw read ex: {ex.Message}"); break; }
            }
        }) { IsBackground = true };
        t.Start();

        Thread.Sleep(2000);

        // 方式 A: Write(byte[], int, int) —— SshBackend 当前用法
        Console.WriteLine("== A: shell.Write(buf,0,len)");
        var a = Encoding.UTF8.GetBytes("echo A_OK_$((7*7))\n");
        shell.Write(a, 0, a.Length);
        Thread.Sleep(1500);

        // 方式 B: Write + Flush
        Console.WriteLine("== B: shell.Write + Flush");
        var b = Encoding.UTF8.GetBytes("echo B_OK_$((8*8))\n");
        shell.Write(b, 0, b.Length);
        shell.Flush();
        Thread.Sleep(1500);

        // 方式 C: WriteLine(string)
        Console.WriteLine("== C: shell.WriteLine(string)");
        shell.WriteLine("echo C_OK_$((9*9))");
        Thread.Sleep(1500);

        // 方式 D: WriteAsync
        Console.WriteLine("== D: shell.WriteAsync");
        var d = Encoding.UTF8.GetBytes("echo D_OK_$((3*3))\n");
        shell.WriteAsync(d, 0, d.Length).Wait();
        Thread.Sleep(1500);

        Console.WriteLine("raw: done");
        cts.Cancel();
        return 0;
    }
}
