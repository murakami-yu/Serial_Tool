using SerialTool.Backends.Ssh;
using Xunit;

namespace SerialTool.Core.Tests;

/// <summary>TOFU 已知主机库：比对/信任/替换/持久化边界。</summary>
public class KnownHostsTests : IDisposable
{
    private readonly string _path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"kh_{Guid.NewGuid():N}.json");

    private KnownHostsStore NewStore() => new(_path);

    [Fact]
    public void 空库_首次连接返回New()
    {
        var s = NewStore();
        Assert.Equal(KnownHostResult.New, s.Verify("192.168.1.10", 22, "ssh-ed25519", "SHA256:abc"));
    }

    [Fact]
    public void 信任后_同指纹返回Trusted_不同指纹返回Changed()
    {
        var s = NewStore();
        s.Trust("192.168.1.10", 22, "ssh-ed25519", "SHA256:abc");
        Assert.Equal(1, s.Entries.Count);

        var s2 = NewStore(); // 重新加载验证落盘
        Assert.Equal(KnownHostResult.Trusted, s2.Verify("192.168.1.10", 22, "ssh-ed25519", "SHA256:abc"));
        Assert.Equal(KnownHostResult.Changed, s2.Verify("192.168.1.10", 22, "ssh-ed25519", "SHA256:xyz"));
    }

    [Fact]
    public void 指纹变更后重新Trust_替换旧记录不累积()
    {
        var s = NewStore();
        s.Trust("192.168.1.10", 22, "ssh-ed25519", "SHA256:old");
        s.Trust("192.168.1.10", 22, "ssh-rsa", "SHA256:new");
        Assert.Single(s.Entries);
        Assert.Equal(KnownHostResult.Trusted, s.Verify("192.168.1.10", 22, "ssh-rsa", "SHA256:new"));
    }

    [Fact]
    public void 主机匹配忽略大小写_端口不同视为不同主机()
    {
        var s = NewStore();
        s.Trust("MyHost.Local", 22, "ssh-ed25519", "SHA256:abc");
        Assert.Equal(KnownHostResult.Trusted, s.Verify("myhost.local", 22, "ssh-ed25519", "SHA256:abc"));
        Assert.Equal(KnownHostResult.New, s.Verify("myhost.local", 2222, "ssh-ed25519", "SHA256:abc"));
    }

    [Fact]
    public void Remove_移除记录后回到首次连接()
    {
        var s = NewStore();
        s.Trust("h", 22, "ssh-ed25519", "SHA256:abc");
        s.Remove("h", 22);
        Assert.Empty(s.Entries);
        Assert.Equal(KnownHostResult.New, s.Verify("h", 22, "ssh-ed25519", "SHA256:abc"));
    }

    [Fact]
    public void 损坏文件_按空库处理且能Trust恢复()
    {
        System.IO.File.WriteAllText(_path, "{ 不是合法 JSON");
        var s = NewStore();
        Assert.Empty(s.Entries);
        s.Trust("h", 22, "ssh-ed25519", "SHA256:abc");
        Assert.Equal(KnownHostResult.Trusted, new KnownHostsStore(_path).Verify("h", 22, "ssh-ed25519", "SHA256:abc"));
    }

    public void Dispose()
    {
        try { System.IO.File.Delete(_path); } catch { }
    }
}
