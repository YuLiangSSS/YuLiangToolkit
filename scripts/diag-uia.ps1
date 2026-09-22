# ASCII-only diagnostic: dump window/controls of the app (same runner as e2e script)
param(
    [string]$Exe = "C:\Users\16320\Desktop\Project\YuLiangTookit\bin\Debug\net9.0-windows\YuLiangTookit.exe"
)

$ErrorActionPreference = "Stop"
Remove-Item Env:YLT_SHOT -ErrorAction SilentlyContinue
$env:YLT_NAV_TOOL = "video-to-audio"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

$p = Start-Process -FilePath $Exe -PassThru
Start-Sleep -Seconds 6
"exited=$($p.HasExited)"

$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$p.Id)
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
"window count: $($wins.Count)"
foreach ($w in $wins) {
    "WINDOW: '$($w.Current.Name)' handle=$($w.Current.NativeWindowHandle)"
    $all = $w.FindAll([System.Windows.Automation.TreeScope]::Descendants, ([System.Windows.Automation.Condition]::TrueCondition))
    "  descendants: $($all.Count)"
    $i = 0
    foreach ($e in $all) {
        if ($i -ge 60) { break }
        $n = $e.Current.Name
        $ct = $e.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        if ($n) { "  [$ct] $n" }
        $i++
    }
}
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
"=== done ==="
