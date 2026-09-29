Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$app = Get-Process SerialTool.App
foreach ($w in $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.ProcessId -eq $app.Id) {
        foreach ($d in $w.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Document)))) {
            try {
                $t = $d.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(100000)
                if ($t -match '[0-9A-F]{2}( [0-9A-F]{2}){8,}') {
                    $bytes = [byte[]]($t.Trim() -split '\s+' | Where-Object { $_ -match '^[0-9A-F]{2}$' })
                    $ascii = [System.Text.Encoding]::UTF8.GetString($bytes)
                    $ascii = $ascii -replace "[^\x20-\x7E\n]", '.'
                    Write-Output ("decoded RX ({0} bytes):" -f $bytes.Count)
                    Write-Output $ascii
                }
            } catch {}
        }
    }
}
