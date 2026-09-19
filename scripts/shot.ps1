# 窗口截图工具 v2：先 SetProcessDPIAware（物理像素坐标系），再按标题找窗口截图。
# 用法: powershell -NoProfile -File shot.ps1 -Title "终端" -Out "C:\tmp\t.png"
param(
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][string]$Out,
    [int]$Pad = 0
)
$src = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32S {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing
Add-Type -AssemblyName System.Drawing
$null = [Win32S]::SetProcessDPIAware()

$found = $null
$cb = [Win32S+EnumWindowsProc]{
    param($hWnd, $lParam)
    if (-not [Win32S]::IsWindowVisible($hWnd)) { return $true }
    $sb = New-Object System.Text.StringBuilder 256
    [void][Win32S]::GetWindowText($hWnd, $sb, 256)
    if ($sb.ToString() -like "*$Title*") {
        $r = New-Object Win32S+RECT
        [void][Win32S]::GetWindowRect($hWnd, [ref]$r)
        $script:found = @{ Hwnd=$hWnd; Rect=$r; Title=$sb.ToString() }
        return $false
    }
    return $true
}
[void][Win32S]::EnumWindows($cb, [IntPtr]::Zero)
if (-not $found) { Write-Error "window not found: $Title"; exit 1 }
[void][Win32S]::SetForegroundWindow($found.Hwnd)
Start-Sleep -Milliseconds 300
$r = $found.Rect
$x = [Math]::Max(0, $r.Left - $Pad); $y = [Math]::Max(0, $r.Top - $Pad)
$w = $r.Right - $r.Left + 2*$Pad; $h = $r.Bottom - $r.Top + 2*$Pad
$bmp = New-Object System.Drawing.Bitmap $w, $h
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size $w, $h))
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved: $Out  ($w x $h)  title=$($found.Title)"
