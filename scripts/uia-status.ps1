Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae  = [System.Windows.Automation.AutomationElement]
$ct  = [System.Windows.Automation.ControlType]
$app = Get-Process SerialTool.App
foreach ($w in $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.ProcessId -eq $app.Id) {
        foreach ($t in $w.FindAll('Descendants', (New-Object System.Windows.Automation.PropertyCondition($ae::ControlTypeProperty, $ct::Text)))) {
            $n = $t.Current.Name
            if ($n -match '发送|字节|格式|连接|断开|SSH') { Write-Output "text: '$n'" }
        }
    }
}
