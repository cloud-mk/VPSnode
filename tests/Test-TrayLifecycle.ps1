[CmdletBinding()]
param([Parameter(Mandatory)] [string]$Application,
      [Parameter(Mandatory)] [string]$TestData)
$ErrorActionPreference = 'Stop'
$exe = (Resolve-Path -LiteralPath $Application).Path
$testRoot = [IO.Path]::GetFullPath($TestData)
if (Test-Path -LiteralPath $testRoot) { throw "Use a fresh test data directory." }
New-Item -ItemType Directory -Path $testRoot | Out-Null
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class TrayRegistration {
    [StructLayout(LayoutKind.Sequential)] private struct Identifier {
        public uint Size; public IntPtr Window; public uint Icon; public Guid Guid;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref Identifier icon, out Rect rect);
    public static bool Exists(Guid guid) {
        var icon = new Identifier { Size=(uint)Marshal.SizeOf<Identifier>(), Guid=guid };
        return Shell_NotifyIconGetRect(ref icon, out _) >= 0;
    }
}
"@
function Wait-For([scriptblock]$Predicate, [string]$Description) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (& $Predicate) { return }
        Start-Sleep -Milliseconds 90
    } while ($timer.Elapsed.TotalSeconds -lt 12)
    throw "Timed out waiting for $Description"
}
function Open-TestApp {
    $info = [Diagnostics.ProcessStartInfo]::new($exe)
    $info.UseShellExecute = $false
    $info.WindowStyle = 'Hidden'
    $info.Environment['VPSNODE_DATA'] = $testRoot
    $info.Environment.Remove('DOTNET_STARTUP_HOOKS') | Out-Null
    return [Diagnostics.Process]::Start($info)
}
function Invoke-AppButton([Diagnostics.Process]$Process, [string]$Name) {
    $Process.Refresh()
    $window = [System.Windows.Automation.AutomationElement]::FromHandle($Process.MainWindowHandle)
    $condition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    $button = $window.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if (!$button) { throw "Missing button $Name" }
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

$identity = ($exe + '|' + $testRoot).ToUpperInvariant()
$bytes = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))
$guid = [Guid]::new([byte[]]$bytes[0..15])
$process = $null
try {
    $process = Open-TestApp
    Wait-For { !$process.HasExited -and [TrayRegistration]::Exists($guid) } 'initial tray registration'
    # Only terminate the isolated process created above.
    $process.Kill()
    $process.WaitForExit()
    "Explorer still reports the old registration immediately after forced termination: $([TrayRegistration]::Exists($guid))"
    1..3 | ForEach-Object {
        $process = Open-TestApp
        Wait-For { $process.Refresh(); $process.MainWindowHandle -ne 0 -and [TrayRegistration]::Exists($guid) } 'relaunch and stable tray registration'
        Invoke-AppButton $process '设置'
        Invoke-AppButton $process '退出应用'
        if (!$process.WaitForExit(12000) -or $process.ExitCode -ne 0) { throw 'Desktop app did not exit cleanly.' }
        Wait-For { ![TrayRegistration]::Exists($guid) } 'icon removal'
        "PASS launch and clean exit $_"
    }
} finally {
    if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
}
