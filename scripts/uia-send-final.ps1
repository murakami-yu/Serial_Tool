param([string]$Text = "echo FINAL_GUI_OK_$((6*7))")
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae  = [System.Windows.Automation.AutomationElement]
$psc = [System.Windows.Automation.PropertyCondition]
$ct  = [System.Windows.Automation.ControlType]
$app = Get-Process SerialTool.App
$win = $null
foreach ($w in $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($w.Current.ProcessId -eq $app.Id) { $win = $w; break }
}
if (-not $win) { throw "app window not found" }

$edits = @($win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Edit))))
# TxInput = edits[1]（多行发送输入框，前面 dump 已验证）
$edits[1].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
Start-Sleep -Milliseconds 400
foreach ($b in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Button)))) {
    if ($b.Current.Name -eq [string]([char]0x53D1 + [char]0x9001)) {
        if ($b.Current.IsEnabled) {
            $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
            Write-Output "send invoked: $Text"
        } else { Write-Output "send button DISABLED" }
        break
    }
}
Start-Sleep -Seconds 2
# RX RichTextBox 回显检查（Document ControlType）
foreach ($d in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Document)))) {
    try {
        $t = $d.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(20000)
        if ($t -like '*FINAL_GUI_OK*') { Write-Output "RX ECHO FOUND"; $hit = $true }
    } catch {}
}
if (-not $hit) { Write-Output "RX echo not found (check log)" }
