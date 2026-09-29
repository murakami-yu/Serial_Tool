param()
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
$ae  = [System.Windows.Automation.AutomationElement]
$psc = [System.Windows.Automation.PropertyCondition]
$ct  = [System.Windows.Automation.ControlType]

function Get-MainWin {
    $wins = $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $wins) { if ($w.Current.Name -like "*Serial Tool*") { return $w } }
    throw "main window not found"
}
$win = Get-MainWin
$edits = @($win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Edit))))
$vals = @(); foreach ($e in $edits) { $v=''; try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}; $vals += $v }

# TxInput：底部发送区多行输入框（ValuePattern 可能不可用，则 SetFocus+SendKeys）
$tx = $edits[0]
try { $tx.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("echo GUI_OK_$((3*3))`n") }
catch { $tx.SetFocus(); [System.Windows.Forms.SendKeys]::SendWait("echo GUI_OK_9{ENTER}") }
Start-Sleep -Milliseconds 300

# 主发送按钮（「发送」Content，发送区行内那个 = 第一个）
$sendBtn = $null
foreach ($b in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Button)))) {
    if ($b.Current.Name -eq ([char]0x53D1 + [char]0x9001 -join '')) { $sendBtn = $b; break }
}
if (-not $sendBtn) { throw "send button not found" }
$sendBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "send clicked"
Start-Sleep 2

# 断开
$dis = [char]0x65AD + [char]0x5F00 -join ''
$conn = [char]0x8FDE + [char]0x63A5 -join ''
$win = Get-MainWin
foreach ($b in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Button)))) {
    if ($b.Current.Name -eq $dis) {
        $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output "disconnect clicked"
        break
    }
}
Start-Sleep 2

# 重连（TOFU 已信任：应无指纹弹窗，直接连上）
$win = Get-MainWin
foreach ($b in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Button)))) {
    if ($b.Current.Name -eq $conn) {
        $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Write-Output "reconnect clicked"
        break
    }
}
Start-Sleep 4
$win = Get-MainWin
foreach ($b in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Button)))) {
    if ($b.Current.Name -eq $dis) { Write-Output "RESULT: RECONNECTED (trusted host, no dialog)"; exit 0 }
}
Write-Output "RESULT: RECONNECT FAILED"; exit 1
