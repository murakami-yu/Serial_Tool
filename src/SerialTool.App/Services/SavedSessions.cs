using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SerialTool.App.Services;

/// <summary>已保存的终端会话（密码/口令绝不落盘）。</summary>
/// <param name="Name">显示名。</param>
/// <param name="Kind">"ssh"（M4 扩展 "telnet"）。</param>
public sealed record SavedSession(
    string Name, string Kind, string Host, int Port, string User, int AuthIndex, string KeyPath);

/// <summary>保存会话列表：Config/terminal_sessions.json（按名称去重）。</summary>
public sealed class SavedSessionsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;
    private List<SavedSession> _items;

    public SavedSessionsStore(string path)
    {
        _path = path;
        _items = Load(path);
    }

    public IReadOnlyList<SavedSession> Items => _items;

    private static List<SavedSession> Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var list = JsonSerializer.Deserialize<List<SavedSession>>(File.ReadAllText(path));
                if (list is not null)
                    return list.Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Host)).ToList();
            }
        }
        catch
        {
            // 损坏按空表处理
        }
        return new List<SavedSession>();
    }

    /// <summary>新增或按名称替换，立即落盘。</summary>
    public void AddOrUpdate(SavedSession session)
    {
        _items.RemoveAll(s => s.Name == session.Name);
        _items.Add(session);
        Save();
    }

    public bool Remove(string name)
    {
        if (_items.RemoveAll(s => s.Name == name) == 0) return false;
        Save();
        return true;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOpts));
        }
        catch
        {
            // 保存失败不影响功能
        }
    }
}
