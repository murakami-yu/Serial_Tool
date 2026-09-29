Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$app = Get-Process SerialTool.App
foreach ($w in $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.ProcessId -eq $app.Id) {
        foreach ($d in $w.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Document)))) {
            $t = ''
            try { $t = $d.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(30000) } catch { $t = '<no text pattern>' }
            $flat = ($t -replace "[\r\n]+", " ~ ")
            $tail = $flat.Substring([Math]::Max(0, $flat.Length - 250))
            Write-Output "doc class='$($d.Current.ClassName)' len=$($t.Length)"
            Write-Output "  tail: $tail"
        }
    }
}