Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$wins = $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)
foreach ($w in $wins) {
    if ($w.Current.Name -like "*Serial Tool*") {
        $cbs = $w.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
            $ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)))
        $i = 0
        foreach ($cb in $cbs) {
            $v = '<none>'
            try {
                $sel = $cb.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
                if ($sel.Count -gt 0) { $v = $sel[0].Current.Name }
            } catch { $v = '<ex>' }
            Write-Output ("combo[{0}] value='{1}'" -f $i, $v); $i++
        }
    }
}
