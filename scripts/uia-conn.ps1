# UIA 驱动主窗「连接」下拉选择连接方式（串口/TCP/SSH）。
# 用法: powershell -NoProfile -File uia-conn.ps1 -Mode "SSH"
param([Parameter(Mandatory=$true)][string]$Mode)
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$auto = [System.Windows.Automation.AutomationElement]
$root = $auto::RootElement
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
$win = $null
foreach ($w in $wins) { if ($w.Current.Name -like "*Serial Tool*") { $win = $w; break } }
if (-not $win) { Write-Error "main window not found"; exit 1 }

# 找所有 ComboBox：串口配置区第 2 个（第 1 个是设备下拉）
$cbCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ComboBox)
$combos = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cbCond)
Write-Output "combos: $($combos.Count)"
$target = $null
foreach ($cb in $combos) {
    # 「连接」下拉的当前值 ∈ {串口, TCP, SSH}；用 Selection 模式读当前项识别
    try {
        $selPat = $cb.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
        $cur = $selPat.Current.GetSelection()
        if ($cur.Count -gt 0 -and @("串口","TCP","SSH") -contains $cur[0].Current.Name) { $target = $cb; break }
    } catch {}
}
if (-not $target) { Write-Error "conn combo not found"; exit 1 }
$ec = $target.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
$ec.Expand()
Start-Sleep -Milliseconds 400
# 展开后的列表项挂在下拉自身子树（Popup 也是其视觉子元素），从下拉节点找
$items = $target.FindAll([System.Windows.Automation.TreeScope]::Descendants,
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
foreach ($it in $items) {
    if ($it.Current.Name -eq $Mode) {
        $sel = $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sel.Select()
        Write-Output "selected: $Mode"
        exit 0
    }
}
Write-Error "item not found: $Mode"; exit 1
