# 在标题匹配窗口内按物理像素偏移点击（窗口内坐标）。
# 用法: powershell -NoProfile -File click.ps1 -Title "终端" -X 77 -Y 84
param(
    [Parameter(Mandatory=$true)][string]$Title,
    [Parameter(Mandatory=$true)][int]$X,
    [Parameter(Mandatory=$true)][int]$Y
)
$src = @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32C {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(int flags, int dx, int dy, int data, int extra);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
Add-Type -TypeDefinition $src
$null = [Win32C]::SetProcessDPIAware()
$found = $null
$cb = [Win32C+EnumWindowsProc]{
    param($hWnd, $lParam)
    if (-not [Win32C]::IsWindowVisible($hWnd)) { return $true }
    $sb = New-Object System.Text.StringBuilder 256
    [void][Win32C]::GetWindowText($hWnd, $sb, 256)
    if ($sb.ToString() -like "*$Title*") {
        $r = New-Object Win32C+RECT
        [void][Win32C]::GetWindowRect($hWnd, [ref]$r)
        $script:found = @{ Rect=$r; Title=$sb.ToString() }
        return $false
    }
    return $true
}
[void][Win32C]::EnumWindows($cb, [IntPtr]::Zero)
if (-not $found) { Write-Error "window not found: $Title"; exit 1 }
$cx = $found.Rect.Left + $X; $cy = $found.Rect.Top + $Y
[void][Win32C]::SetCursorPos($cx, $cy)
Start-Sleep -Milliseconds 120
[Win32C]::mouse_event(0x0002, 0, 0, 0, 0)  # LEFTDOWN
Start-Sleep -Milliseconds 40
[Win32C]::mouse_event(0x0004, 0, 0, 0, 0)  # LEFTUP
Write-Output "clicked ($cx,$cy) in '$($found.Title)'"
