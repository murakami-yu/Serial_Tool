Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae = [System.Windows.Automation.AutomationElement]
$wins = $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)
foreach ($w in $wins) {
    $p = ''
    try { $p = (Get-Process -Id $w.Current.ProcessId).ProcessName } catch {}
    Write-Output ("win: pid={0} proc={1} name='{2}' class='{3}' type={4}" -f $w.Current.ProcessId, $p, $w.Current.Name, $w.Current.ClassName, $w.Current.ControlType.ProgrammaticName)
}
