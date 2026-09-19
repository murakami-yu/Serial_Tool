# UIA 切换主窗复选框（按名称）。用法: powershell -NoProfile -File uia-toggle.ps1 -Name "终端"
param([Parameter(Mandatory=$true)][string]$Name)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$wins = $ae::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
$win = $null
foreach ($w in $wins) { if ($w.Current.Name -like "*Serial Tool*") { $win = $w; break } }
if (-not $win) { Write-Error "main window not found"; exit 1 }
$cond = New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::CheckBox)
foreach ($c in $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)) {
    if ($c.Current.Name -eq $Name) {
        $t = $c.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        $t.Toggle()
        Write-Output "toggled: $Name -> $($t.Current.ToggleState)"
        exit 0
    }
}
Write-Error "checkbox not found: $Name"; exit 1
