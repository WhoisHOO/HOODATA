# HOODATA. start.bat runs this: it starts the engine, opens the dashboard in the browser and shows a small window
# that keeps HOODATA running. Keep this file ASCII-only (Windows PowerShell 5.1 reads BOM-less scripts in the ANSI
# code page); every visible string lives in strings.json.
#
# Test hooks (used by the maintainers, harmless otherwise):
#   HOODATA_LANG=en|ko          force the window language
#   HOODATA_DATA_DIR=<dir>      keep the data somewhere else
#   HOODATA_PORT=<n>            first port to try (default 47100)
#   HOODATA_NO_BROWSER=1        do not open the dashboard
#   HOODATA_SCREENSHOT=<png>    render the window to a PNG once running, then stop
#   HOODATA_RUN_SECONDS=<n>     no window: run for n seconds, then stop

$ErrorActionPreference = 'Stop'
$AppDir = $PSScriptRoot
$Root = Split-Path -Parent $AppDir
$Unattended = [bool]($env:HOODATA_SCREENSHOT -or $env:HOODATA_RUN_SECONDS)

# An App Control or AppLocker policy, as on many work PCs, runs unsigned scripts in ConstrainedLanguage mode, where
# HOODATA cannot run. Explain it in Notepad, which still opens in that mode.
if ($ExecutionContext.SessionState.LanguageMode -ne 'FullLanguage') {
    $notice = Join-Path $AppDir 'restricted.txt'
    if ($Unattended) { throw "PowerShell runs in $($ExecutionContext.SessionState.LanguageMode) mode here; see $notice" }
    Start-Process notepad.exe -ArgumentList "`"$notice`""
    exit 1
}

# Until strings.json is read, a failure is reported in English: start.bat hides this console, so an error that is not
# shown in a message box would leave the user with nothing at all.
$T = $null

function Format-Text([string]$Key) { [string]::Format([string]$T.$Key, [object[]]$args) }

function Show-Fatal([string]$Message) {
    $text = if ($T) { Format-Text 'Fatal' $Message } else { "HOODATA could not start.`n`n$Message" }
    if ($Unattended) { [Console]::Error.WriteLine($text); return }
    try {
        Add-Type -AssemblyName PresentationFramework
        [void][Windows.MessageBox]::Show($text, 'HOODATA', 'OK', 'Error')
    } catch { }
}

try {
    . (Join-Path $AppDir 'HOODATA.Setup.ps1')
    $Strings = Get-Content -LiteralPath (Join-Path $AppDir 'strings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $Lang = if ($env:HOODATA_LANG -in @('ko', 'en')) { $env:HOODATA_LANG }
            elseif ([Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'ko') { 'ko' } else { 'en' }
    $T = $Strings.$Lang
    $DataDir = Resolve-DataDirectory $Root
} catch {
    Show-Fatal $_.Exception.Message
    exit 1
}

# One HOODATA per data folder. A second start opens the running one's dashboard once it answers; when the running
# one is just closing, this start waits for it and then starts normally.
$mutex = New-Object Threading.Mutex($false, ('Local\HOODATA-' + (Get-PathHash $DataDir)))
$owned = $false
try { $owned = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $owned = $true }
if (-not $owned) {
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (-not $owned -and $wait.Elapsed.TotalSeconds -lt 30) {
        $run = Read-RunFile $DataDir
        if ($run -and (Test-Dashboard $run.port)) {
            if (-not $env:HOODATA_NO_BROWSER) { Open-Dashboard $run.port $run.key }
            exit 0
        }
        try { $owned = $mutex.WaitOne(500) } catch [Threading.AbandonedMutexException] { $owned = $true }
    }
    if (-not $owned) { exit 0 }
}
# A run file left by a HOODATA that did not close properly (a crash, Windows shutting down) is stale.
Remove-RunFile $DataDir

$engine = $null
$window = $null

# The run file goes first, so a start while this one is still stopping waits for it instead of opening its dashboard.
function Stop-Hoodata {
    Remove-RunFile $DataDir
    if ($script:engine) {
        try { $script:engine.Stop() } catch { }
        $script:engine = $null
    }
}

function Start-Hoodata {
    Import-HoodataEngine $AppDir
    $e = New-Object Hoodata.Engine($AppDir, $DataDir)
    $e.Lang = $Lang
    $e.LogPath = Join-Path $DataDir 'hoodata.log'
    $port = if ($env:HOODATA_PORT) { [int]$env:HOODATA_PORT } else { 47100 }
    $e.Start($port)
    Write-RunFile $DataDir $e.Port $e.Key
    $e
}

if ($env:HOODATA_RUN_SECONDS) {
    try {
        $engine = Start-Hoodata
        [Console]::Out.WriteLine("HOODATA http://127.0.0.1:$($engine.Port)/")
        Start-Sleep -Seconds ([int]$env:HOODATA_RUN_SECONDS)
    } finally {
        Stop-Hoodata
        $mutex.ReleaseMutex()
    }
    exit 0
}

try {
    Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
    [xml]$xaml = Get-Content -LiteralPath (Join-Path $AppDir 'Window.xaml') -Raw -Encoding UTF8
    $window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
    $ui = @{}
    foreach ($name in 'Version', 'Status', 'Address', 'Folder', 'Warning', 'Hint', 'OpenButton', 'StopButton') { $ui[$name] = $window.FindName($name) }
    $window.Title = 'HOODATA'
    $ui.Status.Text = $T.Starting
    $ui.Hint.Text = $T.Hint
    $ui.OpenButton.Content = $T.OpenDashboard
    $ui.StopButton.Content = $T.Stop
    $ui.OpenButton.IsEnabled = $false
    $ui.Folder.Text = Format-Text 'DataFolderAt' $DataDir
    # Other accounts on this PC may change the files HOODATA runs: say so, and where to put it instead.
    if (Test-SharedWritable $AppDir) {
        $ui.Warning.Text = $T.SharedFolderWarning
        $ui.Warning.Visibility = 'Visible'
    }
} catch {
    Show-Fatal $_.Exception.Message
    $mutex.ReleaseMutex()
    exit 1
}

function Update-Status {
    if (-not $script:engine) { return }
    $err = $script:engine.StoreError
    if ($err) {
        $ui.Status.Text = Format-Text 'StoreError' $err
        $ui.Status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#B42828')
        return
    }
    $midnight = [DateTimeOffset]::new([DateTime]::Today).ToUnixTimeMilliseconds()
    $events = $script:engine.EventsSince($midnight)
    $ui.Status.Text = Format-Text 'Running' $script:engine.ChannelCount $script:engine.RunningSources $events
    $ui.Status.Foreground = [Windows.Media.BrushConverter]::new().ConvertFromString('#1A1A1A')
}

$timer = New-Object Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(2)
$timer.Add_Tick({ try { Update-Status } catch { } })

$window.Add_ContentRendered({
    try {
        $script:engine = Start-Hoodata
        $ui.Version.Text = [Hoodata.Engine]::Version
        $ui.Address.Text = "http://127.0.0.1:$($script:engine.Port)/"
        $ui.OpenButton.IsEnabled = $true
        Update-Status
        $timer.Start()
        if ($env:HOODATA_SCREENSHOT) {
            Save-WindowImage $window $env:HOODATA_SCREENSHOT
            $script:closing = $true
            $window.Close()
            return
        }
        if (-not $env:HOODATA_NO_BROWSER) { Open-Dashboard $script:engine.Port $script:engine.Key }
    } catch {
        Show-Fatal $_.Exception.Message
        $script:failed = $true
        $script:closing = $true
        $window.Close()
    }
})

$ui.OpenButton.Add_Click({ if ($script:engine) { Open-Dashboard $script:engine.Port $script:engine.Key } })
$ui.StopButton.Add_Click({ $window.Close() })

$script:closing = $false
$window.Add_Closing({
    param($sender, $e)
    if ($script:closing -or $Unattended -or -not $script:engine) { return }
    # Closing stops the collection; make sure that is meant while something is being collected.
    if ($script:engine.RunningSources -gt 0) {
        $answer = [Windows.MessageBox]::Show($window, $T.ConfirmClose, 'HOODATA', 'YesNo', 'Question')
        if ($answer -ne 'Yes') { $e.Cancel = $true; return }
    }
    $script:closing = $true
})

$script:failed = $false
try {
    [void]$window.ShowDialog()
} catch {
    Show-Fatal $_.Exception.Message
    $script:failed = $true
} finally {
    $timer.Stop()
    Stop-Hoodata
    $mutex.ReleaseMutex()
}
if ($script:failed) { exit 1 }
