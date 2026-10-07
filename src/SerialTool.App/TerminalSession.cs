using System;
using SerialTool.App.Controls;
using SerialTool.Backends;

namespace SerialTool.App;

/// <summary>终端窗中的一个会话标签：主连接会话（Backend=null，后端归 MainViewModel 管）
/// 或独立会话（自持 backend：SSH 等，关闭标签即断开）。</summary>
public sealed class TerminalSession : IDisposable
{
    /// <summary>标签标题（主连接 = "主连接 · 描述"；独立会话 = "root@192.168.1.10" 等）。</summary>
    public string Title { get; set; }

    public TerminalView View { get; }

    /// <summary>独立会话的后端；主连接会话为 null（字节流经 MainViewModel.RawRxTap 旁路）。</summary>
    public IBusBackend? Backend { get; }

    public bool IsMain => Backend is null;

    /// <summary>状态文本（标签工具提示 / 预留状态点）。</summary>
    public string Status { get; set; } = "";

    // ---------- 实时日志（RTT-T「实时保存」同款）：接收字节 UTF-8 解码后原样落盘 ----------

    private Services.SessionLogger? _logger;
    private System.Text.Decoder? _logDecoder; // 跨接收包的多字节字符状态（防中文截断成乱码）

    // ---------- 时间戳（RTT-T「时间戳」同款）：每行行首注入 [HH:mm:ss.fff]，用户开关 ----------

    private volatile bool _timestamps;
    private bool _tsNeedPrefix = true; // 行首状态（读线程独占写；会话首行即注入）

    /// <summary>时间戳开关：开启后每行行首注入 [HH:mm:ss.fff]（显示/日志/导出一致）。</summary>
    public bool TimestampsEnabled
    {
        get => _timestamps;
        set
        {
            _timestamps = value;
            if (value) _tsNeedPrefix = true; // 开启后下一行即生效
        }
    }

    /// <summary>读线程回调：接收字节流的会话级转换（当前仅时间戳注入）。
    /// 单点转换供 显示+日志 共用，保证两者内容一致。注入只发生在行首（换行后的第一个内容字节前），
    /// 不触碰行内 ANSI 转义与多字节 UTF-8 序列。</summary>
    internal byte[] Transform(ReadOnlySpan<byte> data)
    {
        if (!_timestamps) return data.ToArray();
        var result = new List<byte>(data.Length + 32);
        foreach (var b in data)
        {
            if (_tsNeedPrefix && b != (byte)'\n' && b != (byte)'\r')
            {
                result.AddRange(System.Text.Encoding.ASCII.GetBytes($"[{DateTime.Now:HH:mm:ss.fff}] "));
                _tsNeedPrefix = false;
            }
            result.Add(b);
            if (b == (byte)'\n' || b == (byte)'\r') _tsNeedPrefix = true; // \r\n 只注入一次（\n 到达时仍处行首守卫）
        }
        return result.ToArray();
    }

    /// <summary>实时日志是否开启。</summary>
    public bool Logging => _logger is { IsActive: true };

    /// <summary>当前日志文件路径（未开启为 null）。</summary>
    public string? LogPath => _logger?.FilePath;

    /// <summary>开启实时日志（已开则先停）。返回文件路径。
    /// 开启即倾倒当前终端全部内容（含滚回）与后续实时数据直接拼接——用户规则：
    /// 包含开启前已收到的数据，且不插入任何标记行，文件内容与终端所见完全一致。</summary>
    public string StartLogging(string path)
    {
        StopLogging();
        _logger = new Services.SessionLogger();
        _logger.Open(path);
        try
        {
            var existing = View.ExportAllText(); // UI 线程调用（LogToggle 路径）
            if (existing.Length > 0)
                _logger.Write(existing);
        }
        catch
        {
            // 倾倒失败不影响后续实时记录
        }
        _logDecoder = System.Text.Encoding.UTF8.GetDecoder();
        return path;
    }

    /// <summary>停止实时日志（幂等；Dispose 路径亦调用）。</summary>
    public void StopLogging()
    {
        _logger?.Dispose();
        _logger = null;
        _logDecoder = null;
    }

    /// <summary>读线程回调：接收字节解码后落盘（未开启静默忽略）。</summary>
    private void LogBytes(ReadOnlySpan<byte> data)
    {
        var logger = _logger;
        var decoder = _logDecoder;
        if (logger is null || decoder is null) return;
        try
        {
            var chars = new char[decoder.GetCharCount(data, false)];
            decoder.GetChars(data, chars, false);
            if (chars.Length > 0)
                logger.Write(new string(chars));
        }
        catch
        {
            // 日志绝不影响收发
        }
    }

    private TerminalSession(string title, TerminalView view, IBusBackend? backend)
    {
        Title = title;
        View = view;
        Backend = backend;
    }

    /// <summary>主连接会话：View 已由 MainViewModel 接线（RawRxTap/SendTerminalBytes）。</summary>
    public static TerminalSession ForMain(TerminalView view, string title)
        => new(title, view, null);

    /// <summary>独立 SSH 会话：接线 RX→View、输入→Write、resize→通道窗口变更。</summary>
    public static TerminalSession Ssh(Backends.Ssh.SshBackend backend, TerminalView view, string title)
    {
        var s = new TerminalSession(title, view, backend);
        backend.DataReceived += (_, e) => { var t = s.Transform(e.Bytes); s.LogBytes(t); view.EnqueueBytes(t); };   // 读线程 → 单点转换 → 日志+显示共用
        backend.ErrorOccurred += (_, msg) => view.Dispatcher.BeginInvoke(() =>
        {
            Services.AppLog.Error($"独立 SSH 会话中断（{title}）：{msg}");
            s.Status = "已断开：" + msg;
        });
        view.InputEmitted += bytes =>
        {
            try { backend.Write(bytes); }
            catch { /* 后端已关：忽略（ErrorOccurred 已提示） */ }
        };
        view.Resized += (cols, rows) =>
        {
            if (backend.IsOpen)
                backend.ResizeTerminal(cols, rows);
        };
        return s;
    }

    /// <summary>独立 RTT 会话（J-Link 探针 + SEGGER RTT；无 resize 概念）。
    /// 下行写失败只警一次：目标不消费 RTT 输入是常态故障（缓冲满），逐按键刷日志会淹没。</summary>
    public static TerminalSession Rtt(Backends.Rtt.RttBackend backend, TerminalView view, string title)
    {
        var s = new TerminalSession(title, view, backend);
        backend.DataReceived += (_, e) => { var t = s.Transform(e.Bytes); s.LogBytes(t); view.EnqueueBytes(t); };
        backend.ErrorOccurred += (_, msg) => view.Dispatcher.BeginInvoke(() =>
        {
            Services.AppLog.Error($"独立 RTT 会话中断（{title}）：{msg}");
            s.Status = "已断开：" + msg;
        });
        int writeWarned = 0;
        view.InputEmitted += bytes =>
        {
            try { backend.Write(bytes); }
            catch (Exception ex) when (Interlocked.Exchange(ref writeWarned, 1) == 0)
            {
                view.Dispatcher.BeginInvoke(() =>
                    Services.AppLog.Warn($"RTT 发送失败（{title}，后续同类不再提示）：{ex.Message}"));
            }
        };
        return s;
    }

    /// <summary>独立 Telnet 会话（无 resize 通知：v1 不做 NAWS）。</summary>
    public static TerminalSession Telnet(Backends.Telnet.TelnetBackend backend, TerminalView view, string title)
    {
        var s = new TerminalSession(title, view, backend);
        backend.DataReceived += (_, e) => { var t = s.Transform(e.Bytes); s.LogBytes(t); view.EnqueueBytes(t); };
        backend.ErrorOccurred += (_, msg) => view.Dispatcher.BeginInvoke(() =>
        {
            Services.AppLog.Error($"独立 Telnet 会话中断（{title}）：{msg}");
            s.Status = "已断开：" + msg;
        });
        view.InputEmitted += bytes =>
        {
            try { backend.Write(bytes); }
            catch { }
        };
        return s;
    }

    /// <summary>本地终端会话（ConPTY）：输入管道复用 IBusBackend.Write，resize→ConPTY。</summary>
    public static TerminalSession Local(Services.ConPtySession con, TerminalView view, string title)
    {
        var s = new TerminalSession(title, view, con);
        con.DataReceived += (_, e) => { var t = s.Transform(e.Bytes); s.LogBytes(t); view.EnqueueBytes(t); };
        con.ErrorOccurred += (_, msg) => view.Dispatcher.BeginInvoke(() =>
        {
            Services.AppLog.Info($"本地终端会话退出（{title}）：{msg}");
            s.Status = "已退出：" + msg;
        });
        view.InputEmitted += bytes =>
        {
            try { con.Write(bytes); }
            catch { }
        };
        view.Resized += (cols, rows) => con.Resize(cols, rows);
        return s;
    }

    /// <summary>关闭会话：独立会话断开 backend 并释放视图（主连接会话仅释放视图，连接归主面板管）。</summary>
    public void Dispose()
    {
        StopLogging();
        Backend?.Close();
        Backend?.Dispose();
        View.Dispose();
    }
}
