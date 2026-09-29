param([switch]$Uncheck)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$app = Get-Process SerialTool.App
$win = $null
foreach ($w in $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.ProcessId -eq $app.Id) { $win = $w; break }
}
foreach ($c in $win.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::CheckBox)))) {
    $n = $c.Current.Name
    if ($n -eq 'HEX 发送') {
        $t = $c.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
        Write-Output "checkbox '$n' = $($t.Current.ToggleState)"
        if ($Uncheck -and $t.Current.ToggleState -eq 'On') {
            $t.Toggle()
            Write-Output "  -> toggled off"
        }
    }
}
