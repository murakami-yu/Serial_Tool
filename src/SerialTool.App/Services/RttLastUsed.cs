using System.IO;
using System.Text.Json;

namespace SerialTool.App.Services;

/// <summary>RTT 连接上次使用参数（RTT-T config.json 同款记忆）：Config/rtt_last.json。
/// 对话框打开时自动回填，免去每次重选；S/N 不持久化（以当次探针检测为准）。</summary>
public sealed record RttLastUsed(
    string Device, int SpeedKhz, int Iface, int Channel, bool Reset);

public static class RttLastUsedStore
{
    private static readonly object Gate = new();
    private static string Path => System.IO.Path.Combine(AppContext.BaseDirectory, "Config", "rtt_last.json");

    /// <summary>读取上次参数；无文件/损坏返回 null（对话框用默认值）。</summary>
    public static RttLastUsed? Load()
    {
        try
        {
            if (File.Exists(Path))
                return JsonSerializer.Deserialize<RttLastUsed>(File.ReadAllText(Path));
        }
        catch { /* 损坏按无记忆处理 */ }
        return null;
    }

    /// <summary>连接参数构建成功即落盘；失败静默（记忆绝不影响连接）。</summary>
    public static void Save(RttLastUsed value)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
