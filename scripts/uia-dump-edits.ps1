Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$wins = $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)
foreach ($w in $wins) {
    if ($w.Current.Name -like "*Serial Tool*") {
        $edits = $w.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition(
            $ae::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)))
        $i = 0
        foreach ($e in $edits) {
            $v = ''
            try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
            Write-Output ("{0}: name='{1}' class='{2}' value='{3}'" -f $i, $e.Current.Name, $e.Current.ClassName, $v)
            $i++
        }
    }
}
