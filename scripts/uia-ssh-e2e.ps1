param(
    [string]$SshHost = "127.0.0.1",
    [string]$SshUser = "murakami",
    [string]$SshPassword = "SerialTool@2024"
)
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$ae  = [System.Windows.Automation.AutomationElement]
$psc = [System.Windows.Automation.PropertyCondition]
$ct  = [System.Windows.Automation.ControlType]
Add-Type -Name W -Namespace U -MemberDefinition '[System.Runtime.InteropServices.DllImport("user32.dll")] public static extern bool SetForegroundWindow(System.IntPtr h);'

function Get-MainWin {
    $wins = $ae::RootElement.FindAll('Children', [System.Windows.Automation.Condition]::TrueCondition)
    foreach ($w in $wins) { if ($w.Current.Name -like "*Serial Tool*") { return $w } }
    throw "main window not found"
}
function Find-ByName($root, $type, $name) {
    foreach ($el in $root.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $type)))) {
        if ($el.Current.Name -eq $name) { return $el }
    }
    return $null
}

$win = Get-MainWin
[void][U.W]::SetForegroundWindow($win.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 400

# ---- 1. conn mode combo: current value in {serial,TCP,SSH}; select SSH if needed ----
$combo = $null; $cur = ""
foreach ($cb in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::ComboBox)))) {
    try {
        $sel = $cb.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
        if ($sel.Count -gt 0 -and @([char]0x4E32 + [char]0x53E3 -join '',"TCP","SSH") -contains $sel[0].Current.Name) {
            $combo = $cb; $cur = $sel[0].Current.Name; break
        }
    } catch {}
}
if (-not $combo) { throw "conn combo not found" }
Write-Output "current mode: $cur"
if ($cur -ne "SSH") {
    $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item = $null
    for ($i = 0; $i -lt 10 -and -not $item; $i++) {
        Start-Sleep -Milliseconds 300
        $item = Find-ByName $combo $ct::ListItem "SSH"
        if (-not $item) { $item = Find-ByName $win $ct::ListItem "SSH" }
    }
    if (-not $item) { throw "SSH list item not found" }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Write-Output "mode switched to SSH"
    Start-Sleep -Milliseconds 800
    $win = Get-MainWin
}

# ---- 2. locate SSH fields: port box value '22' -> host box is the edit before it ----
$edits = @($win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Edit))))
$vals = @(); foreach ($e in $edits) {
    $v = ''
    try { $v = $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value } catch {}
    $vals += $v
}
Write-Output ("edits: " + ($vals -join ' | '))
$portIdx = -1
for ($i = 0; $i -lt $vals.Count; $i++) { if ($vals[$i] -eq "22") { $portIdx = $i; break } }
if ($portIdx -lt 1) { throw "SSH port box not found (not in SSH mode?)" }
$hostEdit = $edits[$portIdx + 1]   # layout: port left, host right? verify below
# safer: host = first edit after port whose value contains a dot or equals previous host; user = next text edit
$hostEdit = $null; $userEdit = $null
for ($i = $portIdx + 1; $i -lt $edits.Count; $i++) {
    if (-not $hostEdit -and $vals[$i] -match '\.') { $hostEdit = $edits[$i]; continue }
    if ($hostEdit -and -not $userEdit -and $vals[$i] -and $vals[$i] -notmatch '^\d+$') { $userEdit = $edits[$i]; break }
}
if (-not $hostEdit) { throw "host edit not found" }
if (-not $userEdit) { throw "user edit not found" }
$hostEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($SshHost)
$userEdit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($SshUser)
Write-Output "fields set: host=$SshHost user=$SshUser"

# ---- 3. password via focus + SendKeys ----
$pwdBox = $null
foreach ($el in $win.FindAll('Descendants', [System.Windows.Automation.Condition]::TrueCondition)) {
    if ($el.Current.ClassName -like "*PasswordBox*") { $pwdBox = $el; break }
}
if (-not $pwdBox) { throw "password box not found" }
$pwdBox.SetFocus()
Start-Sleep -Milliseconds 300
$esc = $SshPassword
foreach ($ch in @('+','^','%','~','(',')','{','}','[',']')) { $esc = $esc.Replace($ch, "{$ch}") }
[System.Windows.Forms.SendKeys]::SendWait($esc)
Start-Sleep -Milliseconds 300
Write-Output "password typed"

# ---- 4. click connect button ----
$btn = Find-ByName $win $ct::Button ([char]0x8FDE + [char]0x63A5 -join '')   # "连接"
if (-not $btn) { throw "connect button not found" }
$btn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "connect clicked"

# ---- 5. host key confirm dialog -> trust (poll: dialog is a DESCENDANT of main window) ----
$trustBtn = $null
for ($i = 0; $i -lt 40 -and -not $trustBtn; $i++) {
    Start-Sleep -Milliseconds 300
    $win = Get-MainWin
    foreach ($b in $win.FindAll('Descendants', (New-Object $psc($ae::ControlTypeProperty, $ct::Button)))) {
        if ($b.Current.Name -like "*" + [char]0x4FE1 + [char]0x4EFB + "*") { $trustBtn = $b; break }
    }
}
if ($trustBtn) {
    $trustBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Write-Output "host key trusted"
} else {
    Write-Output "host key dialog not shown (already trusted?)"
}

# ---- 6. verify: button becomes disconnect ----
Start-Sleep -Seconds 2
$win = Get-MainWin
$dis = [char]0x65AD + [char]0x5F00 -join ''   # 断开
if (Find-ByName $win $ct::Button $dis) { Write-Output "RESULT: CONNECTED"; exit 0 }
Write-Error "RESULT: NOT CONNECTED"; exit 1
