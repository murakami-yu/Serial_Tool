# PID 范围窗口工具：按进程名 + 标题包含/排除定位窗口（DPI 感知，物理像素），点击或截图。
# 解决两个坑：1) bash 传中文标题被 ANSI 截断 → 用 -NotTitle 排除主窗即可定位中文标题对话框；
#             2) 跨进程坐标系不一致 → 单脚本内先 SetProcessDPIAware 再量矩形再动作。
# 用法:
#   ui.ps1 -Proc SerialTool.App -Title "Serial Tool" -Action shot -Out C:\tmp\a.png
#   ui.ps1 -Proc SerialTool.App -NotTitle "Serial Tool" -Action shot -Out C:\tmp\b.png
#   ui.ps1 -Proc SerialTool.App -Title "Serial Tool" -Action click -X 55 -Y 1000
param(
    [Parameter(Mandatory=$true)][string]$Proc,
    [string]$Title = "",
    [string]$NotTitle = "",
    [int]$MaxW = 0,
    [int]$MinW = 0,
    [Parameter(Mandatory=$true)][ValidateSet("click","shot")][string]$Action,
    [int]$X = 0,
    [int]$Y = 0,
    [string]$Out = ""
)
$src = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32U {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(int flags, int dx, int dy, int data, int extra);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing
Add-Type -AssemblyName System.Drawing
$null = [Win32U]::SetProcessDPIAware()
$procObj = Get-Process -Name $Proc -ErrorAction SilentlyContinue
if (-not $procObj) { Write-Error "process not found: $Proc"; exit 1 }
$targetPid = $procObj[0].Id
$script:found = $null
$cb = [Win32U+EnumWindowsProc]{
    param($hWnd, $lParam)
    if (-not [Win32U]::IsWindowVisible($hWnd)) { return $true }
    $procId = 0
    [void][Win32U]::GetWindowThreadProcessId($hWnd, [ref]$procId)
    if ($procId -ne $targetPid) { return $true }
    $sb = New-Object System.Text.StringBuilder 256
    [void][Win32U]::GetWindowText($hWnd, $sb, 256)
    $t = $sb.ToString()
    if ($Title -ne "" -and $t -notlike "*$Title*") { return $true }
    if ($NotTitle -ne "" -and $t -like "*$NotTitle*") { return $true }
    if ($t.Length -eq 0) { return $true }  # 无标题 HWND（弹层等）不匹配标题语义
    $r = New-Object Win32U+RECT
    [void][Win32U]::GetWindowRect($hWnd, [ref]$r)
    if ($MaxW -gt 0 -and ($r.Right - $r.Left) -gt $MaxW) { return $true }
    if ($MinW -gt 0 -and ($r.Right - $r.Left) -lt $MinW) { return $true }
    $script:found = @{ Rect=$r; Title=$t }
    return $false
}
[void][Win32U]::EnumWindows($cb, [IntPtr]::Zero)
if (-not $script:found) { Write-Error "window not found (Proc=$Proc Title='$Title' NotTitle='$NotTitle')"; exit 1 }
$r = $script:found.Rect
if ($Action -eq "click") {
    $cx = $r.Left + $X; $cy = $r.Top + $Y
    [void][Win32U]::SetCursorPos($cx, $cy)
    Start-Sleep -Milliseconds 150
    [Win32U]::mouse_event(0x0002, 0, 0, 0, 0)
    Start-Sleep -Milliseconds 50
    [Win32U]::mouse_event(0x0004, 0, 0, 0, 0)
    Write-Output "clicked ($cx,$cy) in '$($script:found.Title)'"
} else {
    if ($Out -eq "") { Write-Error "-Out required for shot"; exit 1 }
    [void][Win32U]::SetForegroundWindow((Get-Process -Id $targetPid).MainWindowHandle)
    Start-Sleep -Milliseconds 300
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size $w, $h))
    $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
    Write-Output "saved: $Out ($w x $h) at $($r.Left),$($r.Top) title='$($script:found.Title)'"
}
