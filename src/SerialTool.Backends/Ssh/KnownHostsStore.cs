using System.Text.Json;

namespace SerialTool.Backends.Ssh;

public sealed record KnownHostEntry(string Host, int Port, string Algorithm, string FingerprintSha256);

public enum KnownHostResult
{
    /// <summary>已保存且指纹一致。</summary>
    Trusted,
    /// <summary>首次连接（无记录）。</summary>
    New,
    /// <summary>有记录但指纹不一致（疑似中间人攻击或服务器重装换钥）。</summary>
    Changed,
}

/// <summary>
/// 主机指纹已知库（TOFU，Trust On First Use），格式对齐 OpenSSH 语义：
/// Host+Port 定位，Algorithm+FingerprintSha256（SHA256:Base64 无填充）为身份。
/// 持久化为 JSON（Config/known_hosts.json，App 层决定路径）。
/// </summary>
public sealed class KnownHostsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private List<KnownHostEntry> _entries;

    public KnownHostsStore(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public IReadOnlyList<KnownHostEntry> Entries => _entries;

    private static List<KnownHostEntry> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<KnownHostEntry>>(File.ReadAllText(path));
                if (list is not null)
                    return list.Where(e => !string.IsNullOrWhiteSpace(e.Host)).ToList();
            }
        }
        catch
        {
            // 文件损坏按空库处理（不覆盖原文件，Trust 时才重写）
        }
        return new List<KnownHostEntry>();
    }

    /// <summary>比对指纹：同主机同指纹 = Trusted；同主机不同指纹 = Changed；无记录 = New。</summary>
    public KnownHostResult Verify(string host, int port, string algorithm, string fingerprintSha256)
    {
        foreach (var e in _entries)
        {
            if (!string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) || e.Port != port)
                continue;
            return string.Equals(e.FingerprintSha256, fingerprintSha256, StringComparison.Ordinal)
                ? KnownHostResult.Trusted
                : KnownHostResult.Changed;
        }
        return KnownHostResult.New;
    }

    /// <summary>信任并记录（新增或替换该主机的旧记录），立即落盘。</summary>
    public void Trust(string host, int port, string algorithm, string fingerprintSha256)
    {
        _entries.RemoveAll(e =>
            string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) && e.Port == port);
        _entries.Add(new KnownHostEntry(host, port, algorithm, fingerprintSha256));
        Save();
    }

    /// <summary>移除指定主机的记录（预留：指纹变更后不信任旧记录）。</summary>
    public void Remove(string host, int port)
    {
        if (_entries.RemoveAll(e =>
                string.Equals(e.Host, host, StringComparison.OrdinalIgnoreCase) && e.Port == port) > 0)
            Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOpts));
        }
        catch
        {
            // 保存失败不影响本次连接（内存库仍有效）
        }
    }
}
