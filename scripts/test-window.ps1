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
$copy = Join-Path $root 'usbPhone.exe'
Copy-Item $Executable $copy
$process = $null
try {
    # A portable fixture from before the rename, with an isolated ADB substitute.
    $data = New-Item -Path (Join-Path $root 'data') -ItemType Directory
    $adbFolder = New-Item -Path (Join-Path $root 'adb') -ItemType Directory
    @'
import sqlite3, sys
with sqlite3.connect(sys.argv[1]) as db:
    db.executescript("""
        CREATE TABLE contacts (id INTEGER PRIMARY KEY, name TEXT NOT NULL, normalized_phone TEXT NOT NULL UNIQUE,
                               contact_group TEXT NOT NULL DEFAULT '');
        CREATE TABLE call_history (id INTEGER PRIMARY KEY, phone TEXT NOT NULL, contact_name_snapshot TEXT NOT NULL,
                                   device_serial TEXT NOT NULL, created_at TEXT NOT NULL, command_status TEXT NOT NULL);
        INSERT INTO contacts (name, normalized_phone) VALUES ('Test Contact', '89010000000');
        PRAGMA user_version = 2;
    """)
'@ | python - (Join-Path $data.FullName 'telphone.db')
    if ($LASTEXITCODE -ne 0) { throw 'Could not prepare the portable contact database.' }

    Add-Type -OutputAssembly (Join-Path $adbFolder.FullName 'adb.exe') -OutputType ConsoleApplication -TypeDefinition @'
using System;
public static class FakeAdb
{
    public static int Main(string[] args)
    {
        string command = string.Join(" ", args);
        if (command == "devices -l")
        {
            Console.WriteLine("List of devices attached");
            Console.WriteLine("USBTEST device model:Test_Device transport_id:1");
            return 0;
        }
        if (command == "-s USBTEST shell am start -a android.intent.action.CALL -d tel:89010000000")
        {
            Console.WriteLine("Starting: Intent");
            return 0;
        }
        return 1;
    }
}
'@
    $process = Start-Process $copy -WorkingDirectory $root -PassThru
    Wait-Until {
        $process.Refresh()
        if ($process.HasExited) { throw "usbPhone exited with code $($process.ExitCode)." }
        $process.MainWindowHandle -ne 0
    } 'usbPhone window did not appear.'

    $handle = $process.MainWindowHandle
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    if ($window.Current.Name -ne 'usbPhone') { throw 'Window title was not renamed.' }
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
        if ($process.HasExited) { throw "usbPhone closed while selecting $name." }
    }

    $window.GetCurrentPattern([System.Windows.Automation.WindowPattern]::Pattern).SetWindowVisualState(
        [System.Windows.Automation.WindowVisualState]::Maximized)
    Wait-Until {
        $contacts = Find-Control $window 'ContactList' ([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
        $dialer = Find-Control $window 'Номер телефона'
        $contacts -and !$contacts.Current.IsOffscreen -and $dialer -and !$dialer.Current.IsOffscreen
    } 'Maximized layout did not show the dialer and contacts.'
    $contacts = Find-Control $window 'ContactList' ([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
    $contactCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $item = $contacts.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $contactCondition)
    if (!$item) { throw 'The contact from the existing database was not shown.' }
    $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $call = Find-Control $window 'Набрать номер'
    Wait-Until { $call.Current.IsEnabled } 'Contact call did not become available.'
    $call.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    Wait-Until {
        $label = Find-Control $window 'DialContactName' ([System.Windows.Automation.AutomationElement]::AutomationIdProperty)
        $label -and !$label.Current.IsOffscreen -and $label.Current.Name -eq 'Кому: Test Contact'
    } 'The name of the contact being called was not shown.'
    $dialer = Find-Control $window 'Номер телефона'
    if ($dialer.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -ne '89010000000') {
        throw 'The dialer did not show the number of the contact being called.'
    }
    Wait-Until { Find-Control $window 'Команда вызова принята телефоном.' } 'The fake ADB call did not finish.'
    Write-Host 'WPF launch, existing portable data, contact name and number, mini mode, topmost, themes and maximized layout passed.'
}
finally {
    if ($process -and !$process.HasExited) {
        $null = $process.CloseMainWindow()
        if (!$process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
    }
    Remove-Item -Recurse -Force $root
}
