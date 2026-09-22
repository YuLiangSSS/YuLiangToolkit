param([string]$ProcName = "YuLiangToolkit.App")

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Dump-UIA($el, $depth, [int]$maxDepth = 8) {
    if ($depth -gt $maxDepth) { return }
    $indent = '  ' * $depth
    try {
        $ct = $el.Current.ControlType.ProgrammaticName -replace 'ControlType\.', ''
        $name = $el.Current.Name
        if ($name) { Write-Output "$indent[$ct] $name" }
    } catch { }
    try {
        $children = $el.FindAll([System.Windows.Automation.TreeScope]::Children, [System.Windows.Automation.Condition]::TrueCondition)
        foreach ($c in $children) { Dump-UIA $c ($depth + 1) $maxDepth }
    } catch { }
}

$proc = Get-Process -Name $ProcName -ErrorAction Stop | Select-Object -First 1
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $proc.Id)
$win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $cond)
if ($null -eq $win) {
    Write-Output "WINDOW NOT FOUND"
} else {
    Write-Output "=== WINDOW: $($win.Current.Name) ==="
    Dump-UIA $win 0
}
