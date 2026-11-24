param([Parameter(Mandatory)][string]$Executable)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class WindowInfo
{
    [DllImport("user32.dll", EntryPoint="GetWindowLongW")]
    public static extern int GetStyle(IntPtr window, int index);
}
'@

function Find-Control($window, $name, $property = [System.Windows.Automation.AutomationElement]::NameProperty) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        $property, $name)
    $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Wait-Until($condition, [string]$message) {
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    do {
        if (& $condition) { return }
        Start-Sleep -Milliseconds 200
    } while ([DateTime]::UtcNow -lt $deadline)
    throw $message
}

function Toggle($element) {
    if (!$element) { throw 'Toggle control was not found.' }
    $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString())
New-Item -Path $root -ItemType Directory | Out-Null
$copy = Join-Path $root 'TelPhone.exe'
Copy-Item $Executable $copy
$process = $null
try {
    $process = Start-Process $copy -WorkingDirectory $root -PassThru
    Wait-Until {
        $process.Refresh()
        if ($process.HasExited) { throw "TelPhone exited with code $($process.ExitCode)." }
        $process.MainWindowHandle -ne 0
    } 'TelPhone window did not appear.'

    $handle = $process.MainWindowHandle
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $fullHeight = $window.Current.BoundingRectangle.Height
    Toggle (Find-Control $window 'Миниатюрный режим')
    Wait-Until { $window.Current.BoundingRectangle.Height -lt $fullHeight / 2 } 'Mini mode did not resize the window.'
    if (!(Find-Control $window 'Номер телефона')) { throw 'Mini mode did not show the dialer.' }

    Toggle (Find-Control $window 'Поверх окон')
    Wait-Until { ([WindowInfo]::GetStyle($handle, -20) -band 8) -ne 0 } 'Topmost was not applied.'
    Toggle (Find-Control $window 'Поверх окон')
    Wait-Until { ([WindowInfo]::GetStyle($handle, -20) -band 8) -eq 0 } 'Topmost was not cleared.'

    Toggle (Find-Control $window 'Миниатюрный режим')
    Wait-Until { $window.Current.BoundingRectangle.Height -ge $fullHeight } 'Full size was not restored.'
    $theme = Find-Control $window 'Тема оформления'
    foreach ($name in @('Тёмная', 'Ауспекс', 'Светлая')) {
        $theme.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
        $item = Find-Control $theme $name
        if (!$item) { throw "Theme option was not found: $name" }
        $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        if ($process.HasExited) { throw "TelPhone closed while selecting $name." }
    }

    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState(
        [System.Windows.Automation.WindowVisualState]::Maximized)
    Wait-Until {
        $contacts = Find-Control $window 'ContactList' ([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
        $dialer = Find-Control $window 'Номер телефона'
        $contacts -and !$contacts.Current.IsOffscreen -and $dialer -and !$dialer.Current.IsOffscreen
    } 'Maximized layout did not show the dialer and contacts.'
    Write-Host 'WPF launch, mini mode, topmost, theme selection and maximized layout passed.'
}
finally {
    if ($process -and !$process.HasExited) {
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    Remove-Item -Recurse -Force $root
}
