param(
    [string]$Exe = "C:\Users\16320\Desktop\Project\YuLiangTookit\bin\Debug\net9.0-windows10.0.19041.0\YuLiangTookit.exe",
    [string]$InputDir = "C:\Users\16320\Desktop\Project\YuLiangTookit\testdata"
)

$ErrorActionPreference = "Stop"
Remove-Item Env:YLT_SHOT -ErrorAction SilentlyContinue
$env:YLT_NAV_TOOL = "video-to-audio"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Find-ByName($root, $name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}
function Find-All-ByName($root, $name) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    return $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $c)
}

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 5

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) { Write-Output "WINDOW NOT FOUND"; Stop-Process -Id $p.Id -Force; exit 1 }

# 1. 设置源文件夹（第一个 Edit 控件）
$editCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Edit)
$edits = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, $editCond)
if ($edits.Count -eq 0) { Write-Output "NO EDIT FOUND"; Stop-Process -Id $p.Id -Force; exit 1 }
$vp = $edits[0].GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
$vp.SetValue($InputDir)
Write-Output "1) 源文件夹已设置为: $InputDir"

# 2. 点击「扫描文件」
$scanBtn = Find-ByName $win "扫描文件"
$scanBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "2) 已点击「扫描文件」"
Start-Sleep -Seconds 3

# 3. 读取状态文本
$status = Find-ByName $win "就绪"
if ($null -eq $status) { $status = Find-ByName $win "正在扫描…" }
$allTexts = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
$statusLine = ($allTexts | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like "*扫描*" -or $_ -like "*视频文件*" }) -join " | "
Write-Output "3) 扫描后状态: $statusLine"

# 4. 点击「开始转换」
$convBtn = Find-ByName $win "开始转换"
$convBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Write-Output "4) 已点击「开始转换」"
Start-Sleep -Seconds 10

# 5. 读取最终状态
$allTexts2 = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants, (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)))
$finalLine = ($allTexts2 | ForEach-Object { $_.Current.Name } | Where-Object { $_ -like "*完成*" -or $_ -like "*失败*" -or $_ -like "*取消*" }) -join " | "
Write-Output "5) 最终状态: $finalLine"

# 6. 输出目录文件
Write-Output "6) 输出目录内容:"
Get-ChildItem $InputDir -Filter "测试视频.*" | Select-Object Name, Length | Format-Table -AutoSize | Out-String | Write-Output

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
Write-Output "=== done ==="
