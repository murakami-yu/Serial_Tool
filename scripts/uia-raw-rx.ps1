Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$app = Get-Process SerialTool.App
foreach ($w in $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.ProcessId -eq $app.Id) {
        foreach ($d in $w.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Document)))) {
            try {
                $t = $d.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(100000)
                Write-Output "LEN=$($t.Length)"
                Write-Output "HEAD:"
                Write-Output $t.Substring(0, [Math]::Min(400, $t.Length))
                Write-Output "TAIL:"
                Write-Output $t.Substring([Math]::Max(0, $t.Length - 600)).Replace("`r", "").Replace("`n", " / ")
            } catch { Write-Output "ex: $_" }
        }
    }
}
