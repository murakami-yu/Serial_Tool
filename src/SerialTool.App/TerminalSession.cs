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
        backend.DataReceived += (_, e) => view.EnqueueBytes(e.Bytes);        // 读线程 → 线程安全入队
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
        backend.DataReceived += (_, e) => view.EnqueueBytes(e.Bytes);
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
        backend.DataReceived += (_, e) => view.EnqueueBytes(e.Bytes);
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
        con.DataReceived += (_, e) => view.EnqueueBytes(e.Bytes);
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
        Backend?.Close();
        Backend?.Dispose();
        View.Dispose();
    }
}
