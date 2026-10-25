# Shared by the test files: compiles the engine with the dev simulators, and small helpers. Dot-sourced in BeforeAll.

$Root = Split-Path -Parent $PSScriptRoot
$AppDir = Join-Path $Root 'app'
. (Join-Path $AppDir 'HOODATA.Setup.ps1')
Import-HoodataEngine $AppDir -Extra (Join-Path $Root 'dev\eval\Synthetic.cs'), (Join-Path $Root 'dev\sim\Simulators.cs')

function New-TestDir {
    $dir = Join-Path ([IO.Path]::GetTempPath()) ('hoodata-test-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
    [void][IO.Directory]::CreateDirectory($dir)
    $dir
}

function Write-Text([string]$Path, [string]$Text, [int]$CodePage = 65001, [switch]$Bom) {
    $enc = if ($CodePage -eq 65001) { New-Object Text.UTF8Encoding($Bom.IsPresent) } else { [Text.Encoding]::GetEncoding($CodePage) }
    [IO.File]::WriteAllText($Path, $Text, $enc)
}

# Korean text built from code points, so the test files stay ASCII.
function K([int[]]$Codes) { -join ($Codes | ForEach-Object { [char]$_ }) }
$script:Am = K 0xC624, 0xC804        # morning
$script:Pm = K 0xC624, 0xD6C4        # afternoon
$script:Temp = K 0xC628, 0xB3C4      # temperature
$script:When = K 0xC77C, 0xC2DC      # date and time

function Invoke-Http($Engine, [string]$Method, [string]$Path, $Body, [hashtable]$Headers, $Cookies, [string]$HostName) {
    $req = [Net.HttpWebRequest]::Create("http://127.0.0.1:$($Engine.Port)$Path")
    $req.Method = $Method
    $req.AllowAutoRedirect = $false
    if ($Cookies) { $req.CookieContainer = $Cookies }
    if ($HostName) { $req.Host = $HostName }
    if ($Headers) { foreach ($k in $Headers.Keys) { $req.Headers[$k] = $Headers[$k] } }
    if ($null -ne $Body) {
        $bytes = if ($Body -is [byte[]]) { $Body } else { [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Compress -Depth 5)) }
        $req.ContentType = 'application/json'
        $s = $req.GetRequestStream()
        $s.Write($bytes, 0, $bytes.Length)
        $s.Close()
    }
    try { $res = $req.GetResponse() } catch [Net.WebException] { $res = $_.Exception.Response }
    $ms = New-Object IO.MemoryStream
    $res.GetResponseStream().CopyTo($ms)
    $out = [pscustomobject]@{ Status = [int]$res.StatusCode; Bytes = $ms.ToArray(); Headers = $res.Headers }
    $res.Close()
    $out | Add-Member -NotePropertyName Text -NotePropertyValue ([Text.Encoding]::UTF8.GetString($out.Bytes))
    $out
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds = 15) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not (& $Condition) -and $sw.Elapsed.TotalSeconds -lt $Seconds) { Start-Sleep -Milliseconds 100 }
    [bool](& $Condition)
}

function Get-FreePort {
    $l = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, 0)
    $l.Start(); $p = $l.LocalEndpoint.Port; $l.Stop(); $p
}

function Wait-Import($Job) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while (-not $Job.Finished -and $sw.Elapsed.TotalSeconds -lt 60) { Start-Sleep -Milliseconds 100 }
}
