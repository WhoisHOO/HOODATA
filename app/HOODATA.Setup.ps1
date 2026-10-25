# HOODATA setup helpers, dot-sourced by HOODATA.ps1 and by the tests: the data folder, the engine build and the
# run file that lets a second start find the running HOODATA. Keep this file ASCII-only.

function Get-ShortPath([string]$Path) {
    try { (New-Object -ComObject Scripting.FileSystemObject).GetFolder($Path).ShortPath } catch { $null }
}

function Test-AsciiPath([string]$Path) {
    # Without '?': a short-path lookup that finds no 8.3 name can hand back the name with '?' for each letter.
    $Path -match '^[\x20-\x7E]+$' -and $Path -notmatch '[?*]'
}

# The private folders are open only to this user, SYSTEM and Administrators.
$script:PrivateSids = @([Security.Principal.WindowsIdentity]::GetCurrent().User.Value, 'S-1-5-18', 'S-1-5-32-544')

function New-PrivateDirectory([string]$Path) {
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in $script:PrivateSids) {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule (
                    (New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $Path))
    [void][IO.Directory]::CreateDirectory($Path, $security)
}

function Test-PrivateDirectory([string]$Path) {
    # Another user could have made the folder first, to read or swap what goes in it.
    $acl = [IO.Directory]::GetAccessControl($Path)
    if ($script:PrivateSids -notcontains $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value) { return $false }
    if (-not $acl.AreAccessRulesProtected) { return $false }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($script:PrivateSids -notcontains $rule.IdentityReference.Value) { return $false }
    }
    $true
}

function Test-WritableDirectory([string]$Path) {
    try {
        $probe = Join-Path $Path ('.hoodata-' + [guid]::NewGuid().ToString('N'))
        [IO.File]::WriteAllText($probe, '')
        [IO.File]::Delete($probe)
        $true
    } catch { $false }
}

# Add-Type compiles with csc.exe, which cannot use a temp folder whose name has letters outside the ANSI code page,
# such as a Hangul user name on an English Windows. Then use a private folder in ProgramData, or the short name as a
# last resort (the same fix as HOOPICS 0.1.2). The folder holds the code Add-Type compiles and loads, so it sits
# right in ProgramData, where other users may not delete or rename it, and is checked each time.
function Set-CompilerTemp {
    if (Test-AsciiPath ([IO.Path]::GetTempPath())) { return }
    $privateTemp = Join-Path $env:ProgramData "HOODATA-temp-$($script:PrivateSids[0])"
    foreach ($dir in $privateTemp, (Get-ShortPath ([IO.Path]::GetTempPath()))) {
        if (-not $dir -or -not (Test-AsciiPath $dir)) { continue }
        try {
            if ($dir -eq $privateTemp) {
                if (-not [IO.Directory]::Exists($dir)) { New-PrivateDirectory $dir }
                if (-not (Test-PrivateDirectory $dir)) { continue }
            }
            if (-not (Test-WritableDirectory $dir)) { continue }
            $env:TEMP = $dir
            $env:TMP = $dir
            return
        } catch {
            continue
        }
    }
}

# The engine's C# files, compiled together; the tests and dev\eval add their own files with -Extra.
function Import-HoodataEngine([string]$AppDir, [string[]]$Extra = @()) {
    if ('Hoodata.Engine' -as [type]) { return }
    Set-CompilerTemp
    # -LiteralPath: a folder name with [ ] in it is not a wildcard.
    $files = @(Get-ChildItem -LiteralPath (Join-Path $AppDir 'engine') -Filter *.cs | Sort-Object Name | ForEach-Object { $_.FullName }) + $Extra
    Add-Type -Path $files -ReferencedAssemblies System, System.Core, System.Xml, System.Security, System.IO.Compression, System.IO.Compression.FileSystem
}

# A HOODATA data folder this user owns but whose permissions are open (copied, restored from a backup, moved to
# another drive) is made private again instead of being passed over. One owned by someone else is not used, and
# neither is a folder named 'data' that holds something else (it keeps its permissions).
function Protect-OwnDirectory([string]$Path) {
    $entries = @([IO.Directory]::GetFileSystemEntries($Path))
    if ($entries.Count -gt 0 -and -not [IO.File]::Exists((Join-Path $Path 'hoodata.db'))) { return }
    $acl = [IO.Directory]::GetAccessControl($Path)
    $owner = $acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($script:PrivateSids -notcontains $owner) { return }
    $security = New-Object Security.AccessControl.DirectorySecurity
    $security.SetAccessRuleProtection($true, $false)
    foreach ($sid in $script:PrivateSids) {
        $security.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule (
                    (New-Object Security.Principal.SecurityIdentifier $sid), 'FullControl', 'ContainerInherit, ObjectInherit', 'None', 'Allow')))
    }
    [IO.Directory]::SetAccessControl($Path, $security)
}

# True when users other than this one (Users, Authenticated Users, Everyone, another account) may change the files
# HOODATA compiles and runs: then whoever changes them runs code as this user. Extracting under C:\ gives that.
function Test-SharedWritable([string]$Path) {
    $write = [Security.AccessControl.FileSystemRights]'WriteData, AppendData, Delete, ChangePermissions, TakeOwnership'
    $acl = [IO.Directory]::GetAccessControl($Path)
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -ne 'Allow') { continue }
        $sid = $rule.IdentityReference.Value
        if ($script:PrivateSids -contains $sid -or $sid -eq 'S-1-3-0' -or $sid -like 'S-1-5-80-*') { continue }
        if (($rule.FileSystemRights -band $write) -ne 0) { return $true }
    }
    $false
}

# The data stays next to start.bat, where it is easy to find, back up or delete. When that folder cannot be written
# (Program Files, or a folder that Controlled Folder Access protects), it goes to the local app data folder.
function Resolve-DataDirectory([string]$Root) {
    $candidates = @()
    if ($env:HOODATA_DATA_DIR) { $candidates += $env:HOODATA_DATA_DIR }
    else {
        $candidates += Join-Path $Root 'data'
        $candidates += Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'HOODATA\data'
    }
    foreach ($dir in $candidates) {
        try {
            if (-not [IO.Directory]::Exists($dir)) { New-PrivateDirectory $dir }
            if (-not (Test-PrivateDirectory $dir)) { Protect-OwnDirectory $dir }
            if (-not (Test-PrivateDirectory $dir)) { continue }
            if (Test-WritableDirectory $dir) { return [IO.Path]::GetFullPath($dir) }
        } catch {
            continue
        }
    }
    throw "No folder for the data: $($candidates -join ', ')"
}

function Get-PathHash([string]$Path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Path.ToLowerInvariant()))
    ([BitConverter]::ToString($bytes, 0, 8)) -replace '-', ''
}

function Write-RunFile([string]$DataDir, [int]$Port, [string]$Key) {
    $json = @{ port = $Port; key = $Key; pid = $PID } | ConvertTo-Json -Compress
    [IO.File]::WriteAllText((Join-Path $DataDir 'run.json'), $json)
}

function Read-RunFile([string]$DataDir) {
    $path = Join-Path $DataDir 'run.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { [IO.File]::ReadAllText($path) | ConvertFrom-Json } catch { $null }
}

# Whether a HOODATA dashboard answers on the port (without the key it answers 401).
function Test-Dashboard([int]$Port) {
    try {
        $req = [Net.HttpWebRequest]::Create("http://127.0.0.1:$Port/api/info")
        $req.Timeout = 2000
        $res = $req.GetResponse()
        $res.Close()
        return $true
    } catch [Net.WebException] {
        $r = $_.Exception.Response
        return ($r -ne $null -and [int]$r.StatusCode -eq 401)
    } catch { return $false }
}

function Remove-RunFile([string]$DataDir) {
    $path = Join-Path $DataDir 'run.json'
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
}

function Open-Dashboard([int]$Port, [string]$Key) {
    Start-Process "http://127.0.0.1:$Port/open?key=$Key"
}

function Save-WindowImage($Window, [string]$Path) {
    $Window.UpdateLayout()
    $dpi = [Windows.Media.VisualTreeHelper]::GetDpi($Window)
    $w = [int][Math]::Ceiling($Window.ActualWidth * $dpi.DpiScaleX)
    $h = [int][Math]::Ceiling($Window.ActualHeight * $dpi.DpiScaleY)
    $bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new($w, $h, 96 * $dpi.DpiScaleX, 96 * $dpi.DpiScaleY, [Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($Window.Content)
    $encoder = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create($Path)
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
