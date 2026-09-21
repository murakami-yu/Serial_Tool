using System.IO;
using System.Runtime.InteropServices;

namespace SerialTool.App.Services;

/// <summary>
/// ConPTY 本地终端会话（Win10 1809+ 系统 API，P/Invoke 零外部依赖）：
/// 承载 pwsh / powershell / cmd，实现 IBusBackend 事件流，终端视图直接复用。
/// 关闭顺序：先关输入管写端（EOF → shell 退出）→ 等待进程 → 兜底 Terminate →
/// CancelSynchronousIo 取消读线程阻塞读 → 收 ConPTY（Win10 死锁规避，详见 Teardown）。
/// 整套拆卸在线程池执行（Close/Dispose = Task.Run），UI 线程绝不承担。
/// </summary>
public sealed class ConPtySession : SerialTool.Backends.IBusBackend
{
    // ---------- Win32 interop ----------

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        // 注意：8 个 DWORD（含易漏的 dwXSize/dwYSize）——少 2 个会使 STARTUPINFOEXW 尺寸 104≠112，
        // cb 低于最低值 → CreateProcessW 报 87（参数无效）
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput,
        uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList,
        int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags,
        IntPtr attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessHeap();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr HeapAlloc(IntPtr hHeap, uint dwFlags, IntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool HeapFree(IntPtr hHeap, uint dwFlags, IntPtr lpMem);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? lpApplicationName, System.Text.StringBuilder lpCommandLine,
        IntPtr lpProcessAttributes, IntPtr lpThreadAttributes, bool bInheritHandles, uint dwCreationFlags,
        IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite,
        out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    // Win10 关窗死锁规避三件套：CancelSynchronousIo 解除读线程的阻塞 ReadFile（ClosePseudoConsole 的前置条件）
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelSynchronousIo(IntPtr hThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint dwDesiredAccess, bool bInheritHandle, uint dwThreadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    // ---------- 会话实现 ----------

    private IntPtr _hPC, _hInWrite, _hOutRead, _hProc, _hThread, _attrList, _heap;
    private IntPtr _hInRead, _hOutWrite; // 交给 ConPTY 的管道端（会话销毁时收）
    private Thread? _readThread;
    private uint _readTid;              // 读线程 TID（Teardown 里 CancelSynchronousIo 用）
    private volatile bool _running;
    private int _tornDown;              // Close/Dispose 双调用只生效一次

    public string Name => "Local";
    public bool IsOpen => _running;
    public string CommandLine { get; }

    public event EventHandler<SerialTool.Backends.TimedData>? DataReceived;
    public event EventHandler<string>? ErrorOccurred;

    public System.Collections.Generic.IReadOnlyList<SerialTool.Backends.DeviceInfo> Scan()
        => Array.Empty<SerialTool.Backends.DeviceInfo>();

    /// <summary>按优先级选择 shell：PowerShell 7 → Windows PowerShell → cmd。</summary>
    public static string PreferredShell() =>
        File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"))
            ? "pwsh.exe -NoLogo"
            : "powershell.exe -NoLogo";

    public ConPtySession(int cols, int rows) : this(PreferredShell(), cols, rows)
    {
    }

    public ConPtySession(string commandLine, int cols, int rows)
    {
        CommandLine = commandLine;
        var size = new COORD { X = (short)Math.Max(10, cols), Y = (short)Math.Max(2, rows) };

        var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
        if (!CreatePipe(out var inRead, out _hInWrite, ref sa, 0) ||
            !CreatePipe(out _hOutRead, out var outWrite, ref sa, 0))
            throw new InvalidOperationException("ConPTY 管道创建失败");

        var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out _hPC);
        if (hr != 0)
        {
            CloseHandle(inRead);
            CloseHandle(outWrite);
            throw new InvalidOperationException($"CreatePseudoConsole 失败 (hr=0x{hr:X8})——需要 Windows 10 1809+");
        }
        // 交给 ConPTY 的两个管道端保持到会话销毁（提前关闭会切断 ConPTY 通路）
        _hInRead = inRead;
        _hOutWrite = outWrite;

        try
        {
            var attrSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrSize); // 预期失败并回填所需尺寸
            if (attrSize == IntPtr.Zero)
                throw new InvalidOperationException("属性列表尺寸查询失败");
            _heap = GetProcessHeap();
            _attrList = HeapAlloc(_heap, 0, attrSize);
            if (_attrList == IntPtr.Zero ||
                !InitializeProcThreadAttributeList(_attrList, 1, 0, ref attrSize))
                throw new InvalidOperationException("属性列表初始化失败");

            if (!UpdateProcThreadAttribute(_attrList, 0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, _hPC, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException("伪控制台属性设置失败");

            var si = new STARTUPINFOEX { StartupInfo = { cb = Marshal.SizeOf<STARTUPINFOEX>() } };
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES; // 与 Pty.Net 一致：禁止子进程回落到父控制台
            si.lpAttributeList = _attrList; // 属性列表必须经本结构体带给 CreateProcessW（空则报 87）
            if (!CreateProcessW(null, new System.Text.StringBuilder(commandLine),
                    IntPtr.Zero, IntPtr.Zero, false, EXTENDED_STARTUPINFO_PRESENT,
                    IntPtr.Zero, null, ref si, out var pi))
            {
                var err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException($"启动进程失败（{commandLine}，Win32 错误 {err}）");
            }
            _hProc = pi.hProcess;
            _hThread = pi.hThread;
        }
        catch
        {
            TeardownHandles();
            throw;
        }

        _running = true;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "ConPtyRead" };
        _readThread.Start();
    }

    /// <summary>终端网格尺寸变化 → ConPTY resize。</summary>
    public void Resize(int cols, int rows)
        => ResizePseudoConsole(_hPC, new COORD { X = (short)Math.Max(10, cols), Y = (short)Math.Max(2, rows) });

    public void Write(ReadOnlySpan<byte> data)
    {
        if (!_running) throw new InvalidOperationException("本地终端已关闭");
        var buf = data.ToArray();
        if (!WriteFile(_hInWrite, buf, (uint)buf.Length, out _, IntPtr.Zero))
            throw new InvalidOperationException("写入本地终端失败");
    }

    /// <summary>关闭/释放：整套拆卸卸载到线程池——Win10 上 ClosePseudoConsole 存在死锁风险
    ///（见 Teardown 注释），绝不能让调用线程（关窗场景 = UI 线程）承担。</summary>
    public void Close() => System.Threading.Tasks.Task.Run(Teardown);

    public void Dispose() => System.Threading.Tasks.Task.Run(Teardown);

    private void ReadLoop()
    {
        _readTid = GetCurrentThreadId();   // 供 Teardown 取消阻塞读
        var buf = new byte[8192];
        while (_running)
        {
            try
            {
                if (!ReadFile(_hOutRead, buf, (uint)buf.Length, out var n, IntPtr.Zero) || n == 0)
                    break; // ConPTY 关闭 / EOF
                var data = new byte[n];
                Array.Copy(buf, data, n);
                DataReceived?.Invoke(this, new SerialTool.Backends.TimedData(DateTime.Now, data));
            }
            catch (Exception)
            {
                if (_running)
                    ErrorOccurred?.Invoke(this, "本地终端已退出");
                break;
            }
        }
        _running = false;
    }

    private void Teardown()
    {
        // Close + Dispose 会接连入队两次，只生效一次
        if (Interlocked.Exchange(ref _tornDown, 1) == 1) return;
        _running = false;

        if (_hInWrite != IntPtr.Zero) CloseHandle(_hInWrite); // EOF → ConPTY 关闭 → shell 退出
        if (_hProc != IntPtr.Zero)
        {
            if (WaitForSingleObject(_hProc, 3000) != 0)
                TerminateProcess(_hProc, 0);
        }
        // Win10 死锁规避（Win11 已修复，本机不复现）：
        // ClosePseudoConsole 会等管道 I/O 退出，而读线程此时仍阻塞在输出管道的同步 ReadFile 上
        // → ClosePseudoConsole 永不返回 → UI 卡死（2026-09-21 Win10 复现机挂起 dump 实锤路径）。
        // 正解：先 CancelSynchronousIo 取消读线程的阻塞读（ReadFile 立即出错返回 → 读线程退出），再关。
        if (_readTid != 0 && _readThread?.IsAlive == true)
        {
            var hT = OpenThread(0x0001 /*THREAD_TERMINATE*/, false, _readTid);
            if (hT != IntPtr.Zero)
            {
                CancelSynchronousIo(hT);
                CloseHandle(hT);
            }
        }
        _readThread?.Join(1000);
        if (_hPC != IntPtr.Zero) ClosePseudoConsole(_hPC);

        TeardownHandles();
    }

    private void TeardownHandles()
    {
        if (_hOutRead != IntPtr.Zero) CloseHandle(_hOutRead);
        if (_hInRead != IntPtr.Zero) CloseHandle(_hInRead);
        if (_hOutWrite != IntPtr.Zero) CloseHandle(_hOutWrite);
        if (_hProc != IntPtr.Zero) CloseHandle(_hProc);
        if (_hThread != IntPtr.Zero) CloseHandle(_hThread);
        if (_attrList != IntPtr.Zero)
        {
            DeleteProcThreadAttributeList(_attrList);
            if (_heap != IntPtr.Zero) HeapFree(_heap, 0, _attrList);
        }
        _hInWrite = _hOutRead = _hInRead = _hOutWrite = _hProc = _hThread = _attrList = _hPC = IntPtr.Zero;
    }
}
