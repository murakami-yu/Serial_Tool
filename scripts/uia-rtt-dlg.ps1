# RTT 对话框 UI 端到端（未装 J-Link 软件机器的负路径链路）：
#   主窗开终端窗 → + RTT → 对话框探针状态行 → 填器件名 → 连接
#   → 失败 MessageBox 文案含 J-Link 安装指引 → 截屏留档 → 清理。
# 用法: powershell -NoProfile -ExecutionPolicy Bypass -File uia-rtt-dlg.ps1 [-ShotDir C:\tmp]
param([string]$ShotDir = "C:\tmp")
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes,System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$src = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32R {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -TypeDefinition $src
$null = [Win32R]::SetProcessDPIAware()

$app = Get-Process -Name SerialTool.App -ErrorAction SilentlyContinue
if (-not $app) { Write-Output "FAIL: SerialTool.App not running"; exit 1 }
$targetPid = $app[0].Id

# 按 PID 枚举全部可见有标题窗口（owned 窗口 UIA 树枚举不到，走 Win32——uia-blind-owned-window 教训）。
# 枚举回调经 $script: 作用域传结果（ui.ps1 已验证模式，scriptblock 不构成词法闭包）。
$script:winList = New-Object System.Collections.ArrayList
function Get-AppWindows {
    $script:winList.Clear()
    $cb = [Win32R+EnumWindowsProc]{
        param($hWnd, $lParam)
        if (-not [Win32R]::IsWindowVisible($hWnd)) { return $true }
        $procId = 0
        [void][Win32R]::GetWindowThreadProcessId($hWnd, [ref]$procId)
        if ($procId -ne $targetPid) { return $true }
        $sb = New-Object System.Text.StringBuilder 256
        [void][Win32R]::GetWindowText($hWnd, $sb, 256)
        $t = $sb.ToString()
        if ($t.Length -eq 0) { return $true }
        [void]$script:winList.Add(@{ Hwnd = $hWnd; Title = $t })
        return $true
    }
    [void][Win32R]::EnumWindows($cb, [IntPtr]::Zero)
    return $script:winList
}

function Shot([IntPtr]$hwnd, [string]$out) {
    $r = New-Object Win32R+RECT
    [void][Win32R]::GetWindowRect($hwnd, [ref]$r)
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    if ($w -le 0 -or $h -le 0) { return }
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "shot: $out"
}

# "终端" / 探针状态判定用的中文字符走码点拼接（防脚本编码问题）
$zhTerminal = [string]([char]0x7EC8 + [char]0x7AEF)
$zhDetecting = [string]([char]0x6B63 + [char]0x5728 + [char]0x68C0 + [char]0x6D4B)  # "正在检测"

$ae = [System.Windows.Automation.AutomationElement]
New-Item -ItemType Directory -Force -Path $ShotDir | Out-Null

# ---- 1. 主窗：点「终端」按钮（2026-09-26 起复选框已改按钮）----
$wins = Get-AppWindows
$main = $wins | Where-Object { $_.Title -like "*Serial Tool*" } | Select-Object -First 1
if (-not $main) { Write-Output "FAIL: main window not found"; exit 1 }
$mainEl = $ae::FromHandle($main.Hwnd)
$btnCond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button)
$termBtn = $null
foreach ($c in $mainEl.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)) {
    if ($c.Current.Name -eq $zhTerminal) { $termBtn = $c; break }
}
if (-not $termBtn) { Write-Output "FAIL: terminal button not found"; exit 1 }
$termBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "PASS: terminal button invoked"
Start-Sleep -Milliseconds 900

# ---- 2. 终端窗：Invoke「+ RTT」----
$wins = Get-AppWindows
$termWin = $wins | Where-Object { $_.Title -eq $zhTerminal } | Select-Object -First 1
if (-not $termWin) { Write-Output "FAIL: terminal window not found"; exit 1 }
$termEl = $ae::FromHandle($termWin.Hwnd)
$rttBtn = $termEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($ae::NameProperty, "+ RTT")))
if (-not $rttBtn) { Write-Output "FAIL: + RTT button not found"; exit 1 }
$rttBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "PASS: + RTT invoked"
Start-Sleep -Milliseconds 900

# ---- 3. RTT 对话框（owned：Win32 定位 + FromHandle）----
$dlg = $null
$deadline = (Get-Date).AddSeconds(5)
while ((Get-Date) -lt $deadline -and -not $dlg) {
    $wins = Get-AppWindows
    $dlg = $wins | Where-Object { $_.Title -like "*RTT*" -and $_.Hwnd -ne $termWin.Hwnd } | Select-Object -First 1
    if (-not $dlg) { Start-Sleep -Milliseconds 200 }
}
if (-not $dlg) { Write-Output "FAIL: RTT dialog not found"; exit 1 }
$dlgEl = $ae::FromHandle($dlg.Hwnd)

# ---- 4. 探针状态行：等待后台检测完成（文案不再是"正在检测…"）----
$probeText = ""
$deadline = (Get-Date).AddSeconds(6)
while ((Get-Date) -lt $deadline) {
    $probe = $dlgEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "ProbeStatus")))
    if ($probe) {
        $probeText = $probe.Current.Name
        if ($probeText -and $probeText -notlike "$zhDetecting*") { break }
    }
    Start-Sleep -Milliseconds 300
}
Write-Output "probe-status: $probeText"

# ---- 5. 填器件名 ----
$deviceBox = $dlgEl.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($ae::AutomationIdProperty, "DeviceBox")))
if (-not $deviceBox) { Write-Output "FAIL: DeviceBox not found"; exit 1 }
$deviceBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("STM32F407VG")
Write-Output "PASS: device filled"
Start-Sleep -Milliseconds 300
Shot $dlg.Hwnd "$ShotDir\rtt-dialog.png"

# ---- 6. 连接（Enter 触发默认按钮）→ 等失败 MessageBox ----
[void][Win32R]::SetForegroundWindow($dlg.Hwnd)
Start-Sleep -Milliseconds 250
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
$msg = $null
$deadline = (Get-Date).AddSeconds(8)
while ((Get-Date) -lt $deadline -and -not $msg) {
    $wins = Get-AppWindows
    $msg = $wins | Where-Object { $_.Title -like "*RTT*" -and $_.Hwnd -ne $termWin.Hwnd -and $_.Hwnd -ne $dlg.Hwnd } | Select-Object -First 1
    if (-not $msg) { Start-Sleep -Milliseconds 200 }
}
if (-not $msg) { Write-Output "FAIL: failure MessageBox not shown"; exit 1 }

# MessageBox 正文：窗口 UIA 树内 Text 元素拼接
$msgEl = $ae::FromHandle($msg.Hwnd)
$texts = $msgEl.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
$msgText = ($texts | ForEach-Object { $_.Current.Name }) -join " "
Write-Output "msgbox: $($msg.Title) | $msgText"
Shot $msg.Hwnd "$ShotDir\rtt-connect-fail.png"

# ---- 7. 断言：失败文案含 J-Link 安装指引（未装软件机器预期路径）----
if ($msgText -like "*J-Link*" -or $msgText -like "*SEGGER*") {
    Write-Output "PASS: failure message mentions J-Link/SEGGER"
} else {
    Write-Output "FAIL: failure message missing J-Link guidance"
}

# ---- 8. 清理：关 MessageBox 与对话框 ----
[void][Win32R]::SetForegroundWindow($msg.Hwnd)
[System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
Start-Sleep -Milliseconds 400
[void][Win32R]::SetForegroundWindow($dlg.Hwnd)
[System.Windows.Forms.SendKeys]::SendWait("{ESC}")
Write-Output "DONE"
exit 0
