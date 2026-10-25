# Pester 5 tests. Run from the repo root:  Invoke-Pester -Path tests
# They compile the engine with Add-Type, as start.bat does, and run it on this PC: SQLite is Windows' own, the
# server listens on 127.0.0.1 on a free port, and the data goes to a temporary folder.

BeforeAll { . (Join-Path $PSScriptRoot 'Helpers.ps1') }

Describe 'Source files' {
    It 'keeps scripts, XAML and C# ASCII-only' {
        # Windows PowerShell and Add-Type's csc read BOM-less files in the ANSI code page.
        $files = Get-ChildItem $Root -Recurse -Include *.ps1, *.psm1, *.cs, *.xaml, *.bat | Where-Object { $_.FullName -notmatch '\\data\\' }
        foreach ($f in $files) {
            $bytes = [IO.File]::ReadAllBytes($f.FullName)
            ($bytes | Where-Object { $_ -gt 127 }).Count | Should -Be 0 -Because $f.Name
        }
    }

    It 'has the same text keys in Korean and English' {
        $strings = Get-Content -Raw -Encoding UTF8 (Join-Path $AppDir 'strings.json') | ConvertFrom-Json
        $ko = $strings.ko.PSObject.Properties.Name | Sort-Object
        $en = $strings.en.PSObject.Properties.Name | Sort-Object
        Compare-Object $ko $en | Should -BeNullOrEmpty
    }

    It 'has a text for every key the dashboard and the window use' {
        $strings = Get-Content -Raw -Encoding UTF8 (Join-Path $AppDir 'strings.json') | ConvertFrom-Json
        $keys = $strings.en.PSObject.Properties.Name
        $js = Get-Content -Raw (Join-Path $AppDir 'web\app.js')
        $used = [regex]::Matches($js, "\b[TF]\('([A-Za-z_]+)'") | ForEach-Object { $_.Groups[1].Value }
        $ps = Get-Content -Raw (Join-Path $AppDir 'HOODATA.ps1')
        $used += [regex]::Matches($ps, '\$T\.(\w+)') | ForEach-Object { $_.Groups[1].Value }
        $used += [regex]::Matches($ps, "Format-Text '(\w+)'") | ForEach-Object { $_.Groups[1].Value }
        # Keys put together in code.
        foreach ($k in 'gap', 'spike', 'step', 'stuck', 'range', 'noisy', 'unusual') { $used += "Kind_$k"; $used += "KindHelp_$k" }
        foreach ($s in 'ok', 'warn', 'alert', 'learning', 'idle', 'paused') { $used += "Status_$s" }
        $used += 'ShowHidden', 'HideHidden', 'StoreError', 'DataFolderAt', 'SharedFolderWarning', 'ErrGeneric'
        foreach ($s in 'pc', 'file', 'http', 'mqtt', 'modbus', 'serial', 'xlsx', 'folder') { $used += "Kind_src_$s" }
        foreach ($r in '1h', '6h', '24h', '7d', '30d', 'all') { $used += "Range_$r" }
        foreach ($k in 'cpu', 'memory', 'disk', 'net_in', 'net_out', 'battery', 'plugged') { $used += "Pc_$k" }
        foreach ($k in 'port', 'lan', 'host', 'tls', 'user', 'password', 'topics', 'qos', 'retained', 'unit', 'interval', 'baud', 'mode', 'parity', 'path', 'pattern') { $used += "F_$k" }
        foreach ($k in 'text', 'rtu', 'none', 'even', 'odd', 'holding', 'input', 'coil', 'discrete', 'big', 'little') { $used += "Opt_$k" }
        foreach ($k in 'http', 'mqtt', 'modbus', 'serial', 'folder') { $used += "Help_$k"; $used += "AddHelp_$k" }
        foreach ($k in 'running', 'stopped', 'error', 'idle', 'connecting') { $used += "SourceState_$k" }
        # A key followed by + is put together in code ('Kind_' + kind); those are listed above.
        $missing = $used | Sort-Object -Unique | Where-Object { $_ -notmatch '_$' -and $keys -notcontains $_ }
        $missing | Should -BeNullOrEmpty
    }
}

Describe 'JSON' {
    It 'reads and writes values' {
        $bs = [string][char]92
        $o = [Hoodata.Json]::Parse('{"a":1.5,"b":[true,null,"x' + $bs + 'u00e9"],"c":{"d":-2e3}}')
        $o['a'] | Should -Be 1.5
        $o['b'][0] | Should -BeTrue
        $o['b'][2] | Should -Be ('x' + [char]0xE9)
        $o['c']['d'] | Should -Be -2000
    }

    It 'escapes angle brackets, so a value cannot end a script block' {
        $bs = [string][char]92
        [Hoodata.Json]::Quote('</script>') | Should -Be ('"' + $bs + 'u003c/script' + $bs + 'u003e"')
    }

    It 'rejects broken JSON' {
        $v = $null
        [Hoodata.Json]::TryParse('{"a":', [ref]$v) | Should -BeFalse
    }
}

Describe 'Database' {
    BeforeAll { $dir = New-TestDir; $store = New-Object Hoodata.Store (Join-Path $dir 'test.db') }
    AfterAll { $store.Dispose(); Remove-Item -Recurse -Force $dir }

    It 'stores channels, values and summaries' {
        $src = $store.AddSource('file', 'test', '{}', $true)
        $ch = $store.AddChannel($src.Id, 'temp', 'Temperature', 'C')
        $samples = New-Object 'System.Collections.Generic.List[Hoodata.Sample]'
        foreach ($i in 0..9) { $samples.Add((New-Object Hoodata.Sample($ch.Id, (1000 * $i), $i))) }
        $store.Write($samples, (New-Object 'System.Collections.Generic.List[Hoodata.MinuteRow]'))
        $r = $store.Read()
        try { $r.Db.ScalarLong("SELECT count(*) FROM sample WHERE ch = $($ch.Id)", -1) | Should -Be 10 } finally { $r.Dispose() }
    }

    It 'keeps one value per channel and time' {
        $ch = $store.LoadChannels()[0]
        $samples = New-Object 'System.Collections.Generic.List[Hoodata.Sample]'
        $samples.Add((New-Object Hoodata.Sample($ch.Id, 0, 42)))
        $store.Write($samples, (New-Object 'System.Collections.Generic.List[Hoodata.MinuteRow]'))
        $r = $store.Read()
        try {
            $r.Db.ScalarLong("SELECT count(*) FROM sample WHERE ch = $($ch.Id)", -1) | Should -Be 10
            $r.Db.ScalarText("SELECT v FROM sample WHERE ch = $($ch.Id) AND t = 0") | Should -Be '42.0'
        } finally { $r.Dispose() }
    }

    It 'opens a path with Hangul in it' {
        $sub = Join-Path $dir ($script:Temp + ' data')
        $s2 = New-Object Hoodata.Store (Join-Path $sub 'x.db')
        $s2.Dispose()
        Test-Path -LiteralPath (Join-Path $sub 'x.db') | Should -BeTrue
    }
}

Describe 'Detectors on synthetic data' {
    BeforeAll {
        $script:rows = foreach ($s in [Hoodata.Eval.Synthetic]::All(7)) {
            $events = [Hoodata.Eval.Evaluator]::Run($s, [Hoodata.DetectSettings]::For('normal'))
            [Hoodata.Eval.Evaluator]::Score($s, $events)
        }
    }

    It 'finds every kind of anomaly put in' {
        foreach ($r in $script:rows | Where-Object Truths -gt 0) {
            ($r.Found / $r.Truths) | Should -BeGreaterOrEqual 0.9 -Because "$($r.Scenario) $($r.Kind)"
        }
    }

    It 'stays quiet on clean data' {
        foreach ($r in $script:rows | Where-Object { $_.Scenario -like '*-clean' }) {
            ($r.FalsePositives / $r.Days) | Should -BeLessOrEqual 0.2 -Because "$($r.Scenario) $($r.Kind)"
        }
    }

    It 'does not count a machine switching on as noise' {
        $noisy = $script:rows | Where-Object { $_.Scenario -eq 'machine' -and $_.Kind -eq 'noisy' }
        if ($noisy) { $noisy.Detections | Should -Be 0 }
    }

    It 'explains why, in numbers' {
        $s = [Hoodata.Eval.Synthetic]::Pressure(7, $false)
        $spike = [Hoodata.Eval.Evaluator]::Run($s, [Hoodata.DetectSettings]::For('normal')) | Where-Object Kind -eq 'spike' | Select-Object -First 1
        $spike.Expected | Should -Not -BeNullOrEmpty
        $spike.Score | Should -BeGreaterThan 5
        ([Hoodata.Json]::Parse($spike.Detail))['scale'] | Should -BeGreaterThan 0
    }
}

Describe 'CSV import' {
    BeforeAll { $dir = New-TestDir }
    AfterAll { Remove-Item -Recurse -Force $dir }

    It 'reads a Korean Excel CSV (CP949, AM/PM in Korean, units in the header)' {
        $path = Join-Path $dir 'excel.csv'
        $lines = @("$($script:When),$($script:Temp)(C),Humidity [%]")
        foreach ($i in 0..29) {
            $h = 13 + [Math]::Floor($i / 60); $m = $i % 60
            $lines += "2026-10-25 $($script:Pm) $($h - 12):$('{0:D2}' -f $m):00,$(20 + $i / 10),$(50 + $i)"
        }
        Write-Text $path ($lines -join "`r`n") -CodePage 949
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.EncodingName | Should -Be 'cp949'
        $layout.TimeColumn | Should -Be 0
        $layout.Columns[1].Name | Should -Be $script:Temp
        $layout.Columns[1].Unit | Should -Be 'C'
        $layout.Columns[2].Unit | Should -Be '%'
        $layout.Columns[1].Kind | Should -Be 'number'
        $ms = 0L
        [Hoodata.CsvAnalyzer]::TryRowTime($layout, $layout.Preview[0], [TimeZoneInfo]::Utc, [ref]$ms) | Should -BeTrue
        [Hoodata.Clock]::ToUtc($ms).ToString('yyyy-MM-dd HH:mm') | Should -Be '2026-10-25 13:00'
    }

    It 'reads semicolons with decimal commas' {
        $path = Join-Path $dir 'eu.csv'
        Write-Text $path ("time;value`n" + ((0..9 | ForEach-Object { "2026-01-0$($_ % 9 + 1) 10:00:00;$_,5" }) -join "`n"))
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.Delimiter | Should -Be ([char]';')
        $layout.DecimalComma | Should -BeTrue
        $v = 0.0; $u = $null
        [Hoodata.Values]::TryNumber('3,5', $true, [ref]$v, [ref]$u) | Should -BeTrue
        $v | Should -Be 3.5
    }

    It 'reads epoch seconds, separate date and time columns, and a preamble' {
        $a = Join-Path $dir 'epoch.csv'
        Write-Text $a ("timestamp,v`n" + ((0..19 | ForEach-Object { "$(1792900000 + 60 * $_),$_" }) -join "`n"))
        $la = [Hoodata.CsvAnalyzer]::Analyze($a)
        $la.TimeFormat | Should -Be 'epoch_s'

        $b = Join-Path $dir 'split.csv'
        Write-Text $b ("Logger X-200`nSerial 1234`n`nDate,Time,Level`n" + ((0..19 | ForEach-Object { "2026/10/25,10:$('{0:D2}' -f $_):00,$($_ * 2)" }) -join "`n"))
        $lb = [Hoodata.CsvAnalyzer]::Analyze($b)
        $lb.Skip | Should -Be 3
        $lb.TimeColumn | Should -Be 0
        $lb.ClockColumn | Should -Be 1
        $ms = 0L
        [Hoodata.CsvAnalyzer]::TryRowTime($lb, $lb.Preview[5], [TimeZoneInfo]::Utc, [ref]$ms) | Should -BeTrue
        [Hoodata.Clock]::ToUtc($ms).ToString('HH:mm') | Should -Be '10:05'
    }

    It 'notices a file with the newest rows first' {
        $path = Join-Path $dir 'desc.csv'
        Write-Text $path ("time,v`n" + ((19..0 | ForEach-Object { "2026-10-25 10:$('{0:D2}' -f $_):00,$_" }) -join "`n"))
        ([Hoodata.CsvAnalyzer]::Analyze($path)).Descending | Should -BeTrue
    }

    It 'reads quoted fields with commas, quotes and line breaks' {
        $reader = New-Object Hoodata.CsvReader((New-Object IO.StringReader("a,""b,1"",""say """"hi"""""",""x`ny""`n`n2,3,4,5`n")), [char]',')
        $r1 = $reader.Next()
        $r1.Count | Should -Be 4
        $r1[1] | Should -Be 'b,1'
        $r1[2] | Should -Be 'say "hi"'
        $r1[3] | Should -Be "x`ny"
        ($reader.Next()).Count | Should -Be 4
        $reader.Next() | Should -BeNullOrEmpty
    }
}

Describe 'Engine and dashboard API' {
    BeforeAll {
        $dir = New-TestDir
        $engine = New-Object Hoodata.Engine($AppDir, $dir)
        $engine.Lang = 'en'
        $engine.LogPath = Join-Path $dir 'hoodata.log'
        $engine.Start(0)
        $cookies = New-Object Net.CookieContainer
        $write = @{ 'X-HOODATA' = '1' }
    }
    AfterAll {
        $engine.Stop()
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }

    It 'listens on 127.0.0.1 only' {
        $engine.Port | Should -BeGreaterThan 0
        $l = New-Object Net.Sockets.TcpClient
        { $l.Connect(([Net.Dns]::GetHostName()), $engine.Port) } | Should -Throw
        $l.Close()
    }

    It 'serves the page but not the API without the key' {
        (Invoke-Http $engine GET '/').Status | Should -Be 200
        (Invoke-Http $engine GET '/api/info').Status | Should -Be 401
        (Invoke-Http $engine GET '/open?key=wrong').Status | Should -Be 403
    }

    It 'refuses another host name (DNS rebinding)' {
        (Invoke-Http $engine GET '/' -HostName "attacker.example:$($engine.Port)").Status | Should -Be 421
    }

    It 'sets the key as a cookie and then answers' {
        $r = Invoke-Http $engine GET "/open?key=$($engine.Key)" -Cookies $cookies
        $r.Status | Should -Be 303
        $r.Headers['Set-Cookie'] | Should -Match 'HttpOnly; SameSite=Strict'
        $info = (Invoke-Http $engine GET '/api/info' -Cookies $cookies).Text | ConvertFrom-Json
        $info.version | Should -Be ([Hoodata.Engine]::Version)
    }

    It 'refuses changes without the page header or from another origin' {
        (Invoke-Http $engine POST '/api/settings' @{ sensitivity = 'low' } -Cookies $cookies).Status | Should -Be 403
        (Invoke-Http $engine POST '/api/settings' @{ sensitivity = 'low' } -Cookies $cookies -Headers @{ 'X-HOODATA' = '1'; Origin = 'http://evil.example' }).Status | Should -Be 403
        (Invoke-Http $engine POST '/api/settings' @{ sensitivity = 'high' } -Cookies $cookies -Headers $write).Status | Should -Be 200
        $engine.Settings.Sensitivity | Should -Be 'high'
        [void](Invoke-Http $engine POST '/api/settings' @{ sensitivity = 'normal' } -Cookies $cookies -Headers $write)
    }

    It 'imports a CSV, finds its anomalies and answers for its channel' {
        $path = Join-Path $dir 'upload.csv'
        $start = [DateTime]::new(2026, 10, 20, 0, 0, 0, [DateTimeKind]::Utc)
        $rows = foreach ($i in 0..1439) {
            $v = 20 + [Math]::Sin($i / 100.0) + 0.01 * ($i % 7)
            if ($i -eq 700) { $v = 35 }
            '{0},{1}' -f $start.AddMinutes($i).ToString('yyyy-MM-dd HH:mm:ss'), $v.ToString([Globalization.CultureInfo]::InvariantCulture)
        }
        Write-Text $path ("time,level`n" + ($rows -join "`n"))
        $r = Invoke-Http $engine POST '/api/import?name=upload.csv' ([IO.File]::ReadAllBytes($path)) -Cookies $cookies -Headers $write
        $r.Status | Should -Be 200
        $job = $r.Text | ConvertFrom-Json
        $job.layout.time_column | Should -Be 0
        (Invoke-Http $engine POST "/api/import/$($job.id)/start" @{ zone = 'utc' } -Cookies $cookies -Headers $write).Status | Should -Be 200
        Wait-Import ($engine.JobList() | Where-Object Id -eq $job.id)
        $j = $engine.JobList() | Where-Object Id -eq $job.id
        $j.State | Should -Be 'done'
        $j.Rows | Should -Be 1440

        $ch = $engine.Channels() | Where-Object { $_.Info.Key -eq 'level' }
        $ch | Should -Not -BeNullOrEmpty
        $from = [Hoodata.Clock]::FromUtc($start)
        $series = (Invoke-Http $engine GET "/api/series?ch=$($ch.Info.Id)&from=$from&to=$($from + 86400000)&n=300" -Cookies $cookies).Text | ConvertFrom-Json
        $series.bucket | Should -BeGreaterThan 0
        $series.t.Count | Should -BeGreaterThan 100
        @($series.events | Where-Object kind -eq 'spike').Count | Should -Be 1

        $events = (Invoke-Http $engine GET "/api/events?ch=$($ch.Info.Id)" -Cookies $cookies).Text | ConvertFrom-Json
        $events.by_kind.spike | Should -Be 1
        $stats = (Invoke-Http $engine GET "/api/stats?from=$from&to=$($from + 86400000)" -Cookies $cookies).Text | ConvertFrom-Json
        ($stats.channels | Where-Object ch -eq $ch.Info.Id).n | Should -Be 1440
    }

    It 'exports anomalies as CSV that Excel reads as UTF-8' {
        $r = Invoke-Http $engine GET '/api/events.csv?lang=ko' -Cookies $cookies
        $r.Status | Should -Be 200
        $r.Bytes[0..2] | Should -Be @(0xEF, 0xBB, 0xBF)
        $r.Headers['Content-Disposition'] | Should -Match 'attachment'
    }

    It 'collects from this PC' {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        while (-not ($engine.Channels() | Where-Object { $_.Info.Key -eq 'cpu' }) -and $sw.Elapsed.TotalSeconds -lt 20) { Start-Sleep -Milliseconds 250 }
        $engine.Channels() | Where-Object { $_.Info.Key -eq 'memory' } | Should -Not -BeNullOrEmpty
    }

    It 'deletes a source with its data' {
        $file = $engine.Sources() | Where-Object { $_.Info.Kind -eq 'file' }
        (Invoke-Http $engine DELETE "/api/sources/$($file.Info.Id)" -Cookies $cookies -Headers $write).Status | Should -Be 200
        $engine.Channels() | Where-Object { $_.Info.Key -eq 'level' } | Should -BeNullOrEmpty
    }
}

Describe 'Pattern analysis' {
    BeforeAll {
        $dir = New-TestDir
        $csv = Join-Path $dir 'greenhouse.csv'
        $script:put = [Hoodata.Eval.Synthetic]::GreenhouseCsv(11, $csv)
        $engine = New-Object Hoodata.Engine($AppDir, $dir)
        $engine.Start(0)
        $job = [Hoodata.ImportJob]::Prepare($csv, 'greenhouse.csv')
        $engine.AddJob($job)
        $job.Run($engine)
        $script:key = @{}
        $ids = New-Object 'System.Collections.Generic.List[int]'
        foreach ($c in $engine.Channels()) { $script:key[$c.Info.Id] = $c.Info.Key; if ($c.Info.Key -ne 'cpu') { $ids.Add($c.Info.Id) } }
        $from = [Hoodata.Clock]::FromUtc([DateTime]::new(2026, 1, 5, 0, 0, 0, [DateTimeKind]::Local).ToUniversalTime())
        $script:res = [Hoodata.PatternAnalyzer]::Run($engine.Store, $ids, $from, $from + 21L * 86400000)
        function Find-Rule([string]$Channel, [string]$Kind, [string]$Type, [string]$On) {
            $script:res.Rules | Where-Object {
                $script:key[$_.Ch] -eq $Channel -and $_.Kind -eq $Kind -and $_.When.Count -eq 1 -and
                $_.When[0].Type -eq $Type -and $script:key[$_.When[0].Ch] -eq $On
            }
        }
    }
    AfterAll {
        $engine.Stop()
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }

    It 'finds that the pressure drops out while the pump runs' {
        $rule = Find-Rule 'pressure' 'gap' 'value' 'pump'
        $rule | Should -Not -BeNullOrEmpty
        $rule.When[0].Value | Should -Be 1
        $rule.Lift | Should -BeGreaterThan 10
    }

    It 'finds that the soil sensor spikes when the humidity is high' {
        $rule = Find-Rule 'soil' 'spike' 'high' 'humidity'
        $rule | Should -Not -BeNullOrEmpty
        $rule.Lift | Should -BeGreaterThan 3
    }

    It 'finds that a dropout follows the pump switching on' {
        $seq = $script:res.Sequences | Where-Object { $script:key[$_.ChA] -eq 'pump' -and $_.KindA -eq 'to:1' -and $script:key[$_.ChB] -eq 'pressure' -and $_.KindB -eq 'gap' }
        $seq | Should -Not -BeNullOrEmpty
        $seq.Hits | Should -BeGreaterOrEqual ($script:put[1] * 0.9)
    }

    It 'adds no chance conditions to a real one' {
        $script:res.Rules | Where-Object { $_.When.Count -gt 1 -and ($_.When | Where-Object Type -in 'weekday', 'daytype') } | Should -BeNullOrEmpty
    }

    It 'reports nothing on a channel that has no pattern put in' {
        $script:res.Rules | Where-Object { $script:key[$_.Ch] -eq 'temperature' } | Should -BeNullOrEmpty
    }
}
