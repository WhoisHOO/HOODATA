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

. (Join-Path $AppDir 'HOODATA.Setup.ps1')

$Strings = Get-Content -Raw -Encoding UTF8 (Join-Path $AppDir 'strings.json') | ConvertFrom-Json
$Lang = if ($env:HOODATA_LANG -in @('ko', 'en')) { $env:HOODATA_LANG }
        elseif ([Globalization.CultureInfo]::CurrentUICulture.TwoLetterISOLanguageName -eq 'ko') { 'ko' } else { 'en' }
$T = $Strings.$Lang

function Format-Text([string]$Key) { [string]::Format([string]$T.$Key, [object[]]$args) }

function Show-Fatal([string]$Message) {
    $text = Format-Text 'Fatal' $Message
    if ($Unattended) { [Console]::Error.WriteLine($text); return }
    Add-Type -AssemblyName PresentationFramework
    [void][Windows.MessageBox]::Show($text, 'HOODATA', 'OK', 'Error')
}

try {
    $DataDir = Resolve-DataDirectory $Root
} catch {
    Show-Fatal $_.Exception.Message
    exit 1
}

# One HOODATA per data folder. A second start opens the running one's dashboard instead.
$mutexName = 'Local\HOODATA-' + (Get-PathHash $DataDir)
$created = $false
$mutex = New-Object Threading.Mutex($true, $mutexName, [ref]$created)
if (-not $created) {
    $run = Read-RunFile $DataDir
    if ($run -and -not $env:HOODATA_NO_BROWSER) { Open-Dashboard $run.port $run.key }
    exit 0
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$engine = $null
$window = $null

function Stop-Hoodata {
    if ($script:engine) {
        try { $script:engine.Stop() } catch { }
        $script:engine = $null
    }
    Remove-RunFile $DataDir
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

[xml]$xaml = Get-Content -Raw -Encoding UTF8 (Join-Path $AppDir 'Window.xaml')
$window = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($name in 'Version', 'Status', 'Address', 'Hint', 'OpenButton', 'StopButton') { $ui[$name] = $window.FindName($name) }
$window.Title = 'HOODATA'
$ui.Status.Text = $T.Starting
$ui.Hint.Text = $T.Hint
$ui.OpenButton.Content = $T.OpenDashboard
$ui.StopButton.Content = $T.Stop
$ui.OpenButton.IsEnabled = $false

function Update-Status {
    if (-not $script:engine) { return }
    $midnight = [DateTimeOffset]::new([DateTime]::Today).ToUnixTimeMilliseconds()
    $events = $script:engine.EventsSince($midnight)
    $ui.Status.Text = Format-Text 'Running' $script:engine.ChannelCount $script:engine.RunningSources $events
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
} finally {
    $timer.Stop()
    Stop-Hoodata
    $mutex.ReleaseMutex()
}
if ($script:failed) { exit 1 }
