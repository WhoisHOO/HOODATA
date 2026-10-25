# Pester 5 tests for the defects found in the review before v0.1.0, so they stay fixed.

BeforeAll {
    . (Join-Path $PSScriptRoot 'Helpers.ps1')

    # A detector with its events collected in memory.
    function New-Detector {
        $c = New-Object Hoodata.Eval.Collector
        $d = New-Object Hoodata.Detector($c, [Hoodata.DetectSettings]::For('normal'))
        [pscustomobject]@{ D = $d; Events = $c.Events }
    }

    function Get-Private($Object, [string]$Field) {
        $Object.GetType().GetField($Field, [Reflection.BindingFlags]'NonPublic, Instance').GetValue($Object)
    }
}

Describe 'Storage' {
    BeforeAll { $dir = New-TestDir }
    AfterAll { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }

    It 'writes again after a failed write (a full disk), instead of failing until restart' {
        $store = New-Object Hoodata.Store (Join-Path $dir 'full.db')
        try {
            $w = Get-Private $store 'w'
            $big = New-Object 'System.Collections.Generic.List[Hoodata.Sample]'
            foreach ($i in 0..20000) { $big.Add((New-Object Hoodata.Sample(1, $i, $i))) }
            $none = New-Object 'System.Collections.Generic.List[Hoodata.MinuteRow]'
            $w.Exec('PRAGMA max_page_count = ' + ($w.ScalarLong('PRAGMA page_count', 0) + 2))
            { $store.Write($big, $none) } | Should -Throw '*full*'
            $w.Exec('PRAGMA max_page_count = 1073741823')
            $small = New-Object 'System.Collections.Generic.List[Hoodata.Sample]'
            $small.Add((New-Object Hoodata.Sample(2, 1, 5)))
            $minutes = New-Object 'System.Collections.Generic.List[Hoodata.MinuteRow]'
            $m = New-Object Hoodata.MinuteRow
            $m.Ch = 2; $m.T = 0; $m.N = 1; $m.Mean = 5; $m.Min = 5; $m.Max = 5
            $minutes.Add($m)
            { $store.Write($small, $minutes) } | Should -Not -Throw
            $w.ScalarLong('SELECT count(*) FROM sample WHERE ch = 2', -1) | Should -Be 1
        } finally { $store.Dispose() }
    }

    It 'keeps the stored part of a minute when the two parts cannot be combined' {
        $store = New-Object Hoodata.Store (Join-Path $dir 'huge.db')
        try {
            foreach ($v in 1.7e308, -1.7e308) {
                $m = New-Object Hoodata.MinuteRow
                $m.Ch = 1; $m.T = 60000; $m.N = 2; $m.Mean = $v; $m.Min = $v; $m.Max = $v
                $l = New-Object 'System.Collections.Generic.List[Hoodata.MinuteRow]'
                $l.Add($m)
                { $store.Write((New-Object 'System.Collections.Generic.List[Hoodata.Sample]'), $l) } | Should -Not -Throw
            }
        } finally { $store.Dispose() }
    }

    It 'forgets read positions that an earlier version left behind for deleted folder sources' {
        $path = Join-Path $dir 'orphan.db'
        $store = New-Object Hoodata.Store $path
        $store.SetMeta('folder:42', '{}')
        $store.Dispose()
        $store = New-Object Hoodata.Store $path
        try { $store.GetMeta('folder:42') | Should -BeNullOrEmpty } finally { $store.Dispose() }
    }

    It 'merges a minute written in two parts instead of keeping only the second' {
        $store = New-Object Hoodata.Store (Join-Path $dir 'merge.db')
        try {
            foreach ($part in @(@{ n = 30; mean = 10.0; sd = 0.0; mn = 10; mx = 10 }, @{ n = 30; mean = 20.0; sd = 0.0; mn = 20; mx = 20 })) {
                $m = New-Object Hoodata.MinuteRow
                $m.Ch = 1; $m.T = 60000; $m.N = $part.n; $m.Mean = $part.mean; $m.Sd = $part.sd; $m.Min = $part.mn; $m.Max = $part.mx
                $l = New-Object 'System.Collections.Generic.List[Hoodata.MinuteRow]'
                $l.Add($m)
                $store.Write((New-Object 'System.Collections.Generic.List[Hoodata.Sample]'), $l)
            }
            $r = $store.Read()
            try {
                $r.Db.ScalarText('SELECT n || ''/'' || mean || ''/'' || mn || ''/'' || mx FROM minute WHERE ch = 1') | Should -Be '60/15.0/10.0/20.0'
                [double]$r.Db.ScalarText('SELECT sd FROM minute WHERE ch = 1') | Should -BeGreaterThan 5
            } finally { $r.Dispose() }
        } finally { $store.Dispose() }
    }
}

Describe 'Gaps' {
    It 'reports a source that stays silent after HOODATA starts again, folders too' {
        foreach ($folder in $false, $true) {
            $x = New-Detector
            $t0 = [Hoodata.Clock]::Now() - 3600000
            $x.D.Quiet = $true
            foreach ($i in 0..99) { $x.D.Feed($t0 + 1000 * $i, 1) }
            $x.D.Quiet = $false
            $start = [Hoodata.Clock]::Now()
            $x.D.ResumeFloor = if ($folder) { [long]::MinValue } else { $start }
            $x.D.WatchFrom = $start
            # How often it sends is not known before it sends twice: a batch a minute must not look silent.
            $x.D.Tick($start + 300000)
            $x.Events.Count | Should -Be 0
            $x.D.Tick($start + 660000)
            @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be 1
            $x.Events[0].T0 | Should -Be $(if ($folder) { $t0 + 99000 } else { $start })
        }
    }

    It 'reports a fast stream that stops within seconds, also after earlier outages' {
        $x = New-Detector
        $t = [Hoodata.Clock]::Now()
        foreach ($outage in 70000, 1800000, 0) {
            foreach ($i in 0..299) { $t += 20; $x.D.Arrived($t, $t); $x.D.Feed($t, $i % 5) }
            $before = @($x.Events | Where-Object Kind -eq 'gap').Count
            $x.D.Tick($t + 5000)
            @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be $before
            $x.D.Tick($t + 15000)
            @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be ($before + 1)
            $t += $outage
        }
    }

    It 'does not take a backlog read at once for a fast stream' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now()
        $x.D.WatchFrom = $now
        # A night of one-minute lines, read in two seconds.
        foreach ($i in 0..599) { $x.D.Arrived($now + 4 * $i, $now - 36000000 + 60000 * $i); $x.D.Feed($now - 36000000 + 60000 * $i, $i % 3) }
        $x.D.Tick($now + 2400 + 60000)
        @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be 0
    }

    It 'takes a backlog read in parts as one arrival' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now()
        $x.D.WatchFrom = $now
        # A week of one-minute lines, sent in parts 90 ms apart; the logger's next line is due in a minute.
        $t = $now - 7 * 86400000
        foreach ($part in 0..9) {
            foreach ($k in 0..999) { $x.D.Arrived($now + 90 * $part, $t); $x.D.Feed($t, $k % 3); $t += 60000 }
        }
        $x.D.Tick($now + 900 + 30000)
        @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be 0
    }

    It 'forgets the rhythm when the settings change' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now()
        foreach ($i in 0..59) { $x.D.Arrived($now + 1000 * $i, $now + 1000 * $i); $x.D.Feed($now + 1000 * $i, $i % 3) }
        $x.D.ResetRhythm()
        $t = $now + 60000
        foreach ($i in 0..5) { $t += 20000; $x.D.Arrived($t, $t); $x.D.Feed($t, $i % 3); $x.D.Tick($t + 15000) }
        @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be 0
    }

    It 'places a gap at the last value of a device whose clock lags' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now()
        $x.D.ResumeFloor = $now
        $x.D.WatchFrom = $now
        $lag = 9 * 3600000
        foreach ($i in 0..59) { $x.D.Arrived($now + 1000 * $i, $now + 1000 * $i - $lag); $x.D.Feed($now + 1000 * $i - $lag, $i % 3) }
        $x.D.Tick($now + 59000 + 60000)
        $gap = @($x.Events | Where-Object Kind -eq 'gap')
        $gap.Count | Should -Be 1
        $gap[0].T0 | Should -Be ($now + 59000 - $lag)
    }

    It 'does not learn the time a source was paused as its usual interval' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now()
        foreach ($i in 0..4) { $x.D.Arrived($now + 60000 * $i, $now + 60000 * $i); $x.D.Feed($now + 60000 * $i, $i % 2) }
        $x.D.Suspend()
        $back = $now + 4 * 60000 + 6 * 3600000
        $x.D.WatchFrom = $back
        foreach ($i in 0..2) { $x.D.Arrived($back + 60000 * $i, $back + 60000 * $i); $x.D.Feed($back + 60000 * $i, $i % 2) }
        $x.D.Tick($back + 2 * 60000 + 600000)
        @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be 1
    }

    It 'does not report gaps for a device whose own clock lags the PC' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now()
        foreach ($i in 0..119) {
            $arrival = $now + 1000 * $i
            $x.D.Arrived($arrival, $arrival - 15000)
            $x.D.Feed($arrival - 15000, $i % 3)
            $x.D.Tick($arrival + 500)
        }
        @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be 0
    }

    It 'does not report gaps for a device that sends its readings in batches' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now()
        foreach ($batch in 0..29) {
            $arrival = $now + 60000 * $batch
            foreach ($k in 0..59) {
                $x.D.Arrived($arrival, $arrival - 60000 + 1000 * $k)
                $x.D.Feed($arrival - 60000 + 1000 * $k, $k % 4)
            }
            foreach ($s in 1..59) { $x.D.Tick($arrival + 1000 * $s) }
        }
        @($x.Events | Where-Object Kind -eq 'gap').Count | Should -Be 0
    }

    It 'keeps the length of a gap that is open when the source stops' {
        $x = New-Detector
        $now = [Hoodata.Clock]::Now() - 600000
        foreach ($i in 0..99) { $x.D.Arrived($now + 1000 * $i, $now + 1000 * $i); $x.D.Feed($now + 1000 * $i, 1) }
        $x.D.Tick($now + 100000 + 60000)
        $x.D.Suspend()
        $gap = $x.Events | Where-Object Kind -eq 'gap'
        ($gap.T1 - $gap.T0) | Should -BeGreaterThan 60000
    }
}

Describe 'Device messages' {
    It 'reads "label: value" lines as Arduino sketches print them' {
        foreach ($line in 'Temperature: 23.5', 'temp: 23.5, hum: 61', 'temp = 23.5', 'Humidity: 45.00%  Temperature: 23.00C') {
            ([Hoodata.Payload]::Parse($line, $null)).Count | Should -Be 1 -Because $line
        }
        (([Hoodata.Payload]::Parse('Humidity: 45.00%  Temperature: 23.00C', $null))[0].Values | ForEach-Object Value) -join ',' | Should -Be '45,23'
    }

    It 'reads one JSON object per line, and a CSV with a header and a time column' {
        ([Hoodata.Payload]::Parse("{`"temp`":1}`n{`"temp`":2}", $null)).Count | Should -Be 2
        $p = [Hoodata.Payload]::Parse("time,temp,hum`n2026-10-25T10:00:00Z,23.5,61`n2026-10-25T10:01:00Z,23.6,60", $null)
        $p.Count | Should -Be 2
        $p[0].Time | Should -Be ([Hoodata.Clock]::FromUtc([DateTime]::new(2026, 10, 25, 10, 0, 0, [DateTimeKind]::Utc)))
        ($p[1].Values | ForEach-Object Key) -join ',' | Should -Be 'temp,hum'
    }

    It 'takes the last of several rows without a time, not all of them at one time' {
        $r = [Hoodata.Payload]::ToReadings([Hoodata.Payload]::Parse("temp,hum`n21,50`n22,51`n23,52", $null), $null, 1000)
        ($r | ForEach-Object { "$($_.Key)=$($_.V)@$($_.T)" }) -join ' ' | Should -Be 'temp=23@1000 hum=52@1000'
    }

    It 'keeps one value per line without times, as sketches send them' {
        $r = [Hoodata.Payload]::ToReadings([Hoodata.Payload]::Parse("temp=1`nhum=2", $null), $null, 1000)
        ($r | ForEach-Object Key) -join ',' | Should -Be 'temp,hum'
    }

    It 'keeps a value column named t when it holds no times' {
        $p = [Hoodata.Payload]::Parse("t,h`n23.5,61", $null)
        ($p[0].Values | ForEach-Object Key) -join ',' | Should -Be 't,h'
    }

    It 'takes a timestamp without a zone as local time' {
        $ms = 0L
        [Hoodata.Payload]::TryTime('2026-10-25T10:00:00', [ref]$ms) | Should -BeTrue
        $ms | Should -Be ([Hoodata.Clock]::FromUtc([DateTime]::new(2026, 10, 25, 10, 0, 0, [DateTimeKind]::Local).ToUniversalTime()))
    }

    It 'sends an MQTT password only with a user name' {
        $p = [Hoodata.MqttPacket]::BuildConnect('id', '', 'secret', 60, $true)
        ($p[9] -band 0xC0) | Should -Be 0
        $p = [Hoodata.MqttPacket]::BuildConnect('id', 'u', 'secret', 60, $true)
        ($p[9] -band 0xC0) | Should -Be 0xC0
    }
}

Describe 'Files' {
    BeforeAll {
        $dir = New-TestDir
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        # A workbook from its parts; the sheet XML goes inside <worksheet>, the styles' cellXfs inside <styleSheet>.
        function New-Workbook([string]$Path, [string]$SheetData, [string]$Styles, [string]$Shared) {
            $main = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'
            $parts = @{
                'xl/workbook.xml' = "<workbook xmlns=`"$main`" xmlns:r=`"http://schemas.openxmlformats.org/officeDocument/2006/relationships`"><sheets><sheet name=`"a`" sheetId=`"1`" r:id=`"rId1`"/></sheets></workbook>"
                'xl/_rels/workbook.xml.rels' = '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>'
                'xl/worksheets/sheet1.xml' = "<worksheet xmlns=`"$main`"><sheetData>$SheetData</sheetData></worksheet>" }
            if ($Styles) { $parts['xl/styles.xml'] = "<styleSheet xmlns=`"$main`">$Styles</styleSheet>" }
            if ($Shared) { $parts['xl/sharedStrings.xml'] = "<sst xmlns=`"$main`">$Shared</sst>" }
            if (Test-Path $Path) { Remove-Item $Path }
            $zip = [IO.Compression.ZipFile]::Open($Path, 'Create')
            foreach ($k in $parts.Keys) {
                $w = New-Object IO.StreamWriter($zip.CreateEntry($k).Open())
                $w.Write($parts[$k]); $w.Dispose()
            }
            $zip.Dispose()
        }
        function ConvertFrom-Workbook([string]$Path) {
            $csv = $Path + '.csv'
            [void][Hoodata.Xlsx]::ToCsv($Path, $csv)
            ([IO.File]::ReadAllText($csv) -split "`n") | Where-Object { $_ }
        }
    }
    AfterAll { Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue }

    It 'joins separate date and time columns of an Excel sheet' {
        $csv = Join-Path $dir 'dt.csv'
        [void][Hoodata.Xlsx]::ToCsv((Join-Path $PSScriptRoot 'fixtures\excel-date-time.xlsx'), $csv)
        $layout = [Hoodata.CsvAnalyzer]::Analyze($csv)
        $layout.TimeColumn | Should -Be 0
        $layout.ClockColumn | Should -Be 1
        $times = foreach ($row in $layout.Preview) { $ms = 0L; [void][Hoodata.CsvAnalyzer]::TryRowTime($layout, $row, [TimeZoneInfo]::Utc, [ref]$ms); $ms }
        @($times | Sort-Object -Unique).Count | Should -Be $layout.Preview.Count
        [Hoodata.Clock]::ToUtc($times[1]).ToString('yyyy-MM-dd HH:mm') | Should -Be '2026-03-01 09:10'
    }

    It 'refuses a sheet wider than any table HOODATA takes' {
        $xlsx = Join-Path $dir 'wide.xlsx'
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::Open($xlsx, 'Create')
        $parts = @{
            'xl/workbook.xml' = '<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="a" sheetId="1" r:id="rId1"/></sheets></workbook>'
            'xl/_rels/workbook.xml.rels' = '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>'
            'xl/worksheets/sheet1.xml' = '<worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><dimension ref="A1:ZZZZ1"/><sheetData><row r="1"><c r="A1"><v>1</v></c><c r="BDWGO1"><v>2</v></c></row></sheetData></worksheet>' }
        foreach ($k in $parts.Keys) {
            $e = $zip.CreateEntry($k)
            $s = New-Object IO.StreamWriter($e.Open())
            $s.Write($parts[$k]); $s.Dispose()
        }
        $zip.Dispose()
        { [Hoodata.Xlsx]::ToCsv($xlsx, (Join-Path $dir 'wide.csv')) } | Should -Throw
        (Get-Item (Join-Path $dir 'wide.csv') -ErrorAction SilentlyContinue).Length | Should -BeLessThan 100000
    }

    It 'reads decimal commas in a tab file, column by column' {
        $path = Join-Path $dir 'tab.csv'
        Write-Text $path ("time`ttemp`tflow`n" + ((0..9 | ForEach-Object { "2026-10-25 10:0$($_):00`t21,$_`t0,125" }) -join "`n"))
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.Columns[1].Kind | Should -Be 'number'
        $layout.Columns[2].DecimalComma | Should -BeTrue
        $v = 0.0; $u = $null
        [Hoodata.Values]::TryNumber('0,125', $false, [ref]$v, [ref]$u) | Out-Null
        $v | Should -Not -Be 125
    }

    It 'keeps the time of full timestamps shown with a date-only or time-only format' {
        $xlsx = Join-Path $dir 'shown.xlsx'
        # 46319.375 is 2026-10-24 09:00; style 1 shows a date (14), style 2 a time (20).
        New-Workbook $xlsx '<row r="1"><c r="A1" s="1"><v>46319.375</v></c><c r="B1" s="2"><v>46319.375</v></c><c r="C1" s="1"><v>46319</v></c><c r="D1" s="2"><v>0.375</v></c></row>' `
            '<cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="14"/><xf numFmtId="20"/></cellXfs>'
        ConvertFrom-Workbook $xlsx | Should -Be '2026-10-24 09:00:00,2026-10-24 09:00:00,2026-10-24,09:00:00'
    }

    It 'reads the East Asian built-in date formats by their values' {
        $xlsx = Join-Path $dir 'asian.xlsx'
        New-Workbook $xlsx '<row r="1"><c r="A1" s="1"><v>46319</v></c><c r="B1" s="2"><v>0.375</v></c><c r="C1" s="1"><v>46319.375</v></c></row>' `
            '<cellXfs count="3"><xf numFmtId="0"/><xf numFmtId="34"/><xf numFmtId="55"/></cellXfs>'
        ConvertFrom-Workbook $xlsx | Should -Be '2026-10-24,09:00:00,2026-10-24 09:00:00'
    }

    It 'reads dates stored as ISO text (Strict Open XML)' {
        $xlsx = Join-Path $dir 'iso.xlsx'
        New-Workbook $xlsx '<row r="1"><c r="A1" t="d"><v>2026-10-24T09:10:00.00000000000000000000</v></c><c r="B1" t="d"><v>2026-10-24</v></c><c r="C1" t="d"><v>1899-12-31T09:00:00</v></c></row>'
        ConvertFrom-Workbook $xlsx | Should -Be '2026-10-24 09:10:00,2026-10-24,09:00:00'
    }

    It 'stops a small workbook that unpacks to far more than a sheet holds' {
        $xlsx = Join-Path $dir 'bomb.xlsx'
        $cells = -join (0..999 | ForEach-Object { "<c t=`"s`"><v>0</v></c>" })
        # Each cell repeats a string of 40 000 characters (cut to Excel's 32 767): 33 MB a row.
        New-Workbook $xlsx (-join (1..10 | ForEach-Object { "<row>$cells</row>" })) $null ('<si><t>' + ('x' * 40000) + '</t></si>')
        (Get-Item $xlsx).Length | Should -BeLessThan 100000
        { [Hoodata.Xlsx]::ToCsv($xlsx, (Join-Path $dir 'bomb.csv')) } | Should -Throw '*too_large*'
        (Get-Item (Join-Path $dir 'bomb.csv')).Length | Should -BeLessThan 110MB
    }

    It 'reads 12.345,6 in a semicolon file as a number' {
        $path = Join-Path $dir 'de.csv'
        Write-Text $path ("Zeit;Temperatur;Zaehler_kWh`n" + ((0..9 | ForEach-Object { "2026-10-24 10:0$($_):00;21,$_;12.34$($_),6" }) -join "`n"))
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.Columns[2].Kind | Should -Be 'number'
        $v = 0.0; $u = $null
        [Hoodata.Values]::TryNumber('12.345,6', $true, [ref]$v, [ref]$u) | Should -BeTrue
        $v | Should -Be 12345.6
    }

    It 'takes midnight shown as a date alone in a column of times' {
        $path = Join-Path $dir 'midnight.csv'
        $rows = foreach ($h in 0..23) { $d = [DateTime]::new(2026, 10, 1).AddHours(6 * $h); $(if ($d.Hour -eq 0) { $d.ToString('yyyy-MM-dd') } else { $d.ToString('yyyy-MM-dd HH:mm:ss') }) + ",$h" }
        Write-Text $path ("time,level`n" + ($rows -join "`n"))
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.TimeColumn | Should -Be 0
        $ms = 0L
        [Hoodata.CsvAnalyzer]::TryRowTime($layout, $layout.Preview[0], [TimeZoneInfo]::Utc, [ref]$ms) | Should -BeTrue
        [Hoodata.Clock]::ToUtc($ms).ToString('yyyy-MM-dd HH:mm') | Should -Be '2026-10-01 00:00'
    }

    It 'stops a workbook with a huge cell or a huge string table' {
        $xlsx = Join-Path $dir 'inline.xlsx'
        $cells = -join (0..99 | ForEach-Object { '<c t="inlineStr"><is><t>' + ('a' * 100000) + '</t></is></c>' })
        New-Workbook $xlsx "<row>$cells</row>"
        $out = Join-Path $dir 'inline.csv'
        [void][Hoodata.Xlsx]::ToCsv($xlsx, $out)
        (Get-Item $out).Length | Should -BeLessThan (100 * 32768 + 1000)
        $xlsx = Join-Path $dir 'manysi.xlsx'
        New-Workbook $xlsx '<row><c><v>1</v></c></row>' $null ('<si/>' * 2000000)
        { [Hoodata.Xlsx]::ToCsv($xlsx, (Join-Path $dir 'manysi.csv')) } | Should -Throw '*too_large*'
    }

    It 'reads a Korean file whose only Korean is a unit sign' {
        $cp = [Text.Encoding]::GetEncoding(949)
        $bytes = $cp.GetBytes("time,Temp(" + [char]0x2103 + "),Humi(%RH)`n2026-10-25 10:00:00,21,50`n")
        $name = $null
        [void][Hoodata.CsvAnalyzer]::DetectEncoding($bytes, $bytes.Length, [ref]$name)
        $name | Should -Be 'cp949'
    }

    It 'keeps thousands separators in a tab file from US Excel' {
        $path = Join-Path $dir 'us.tsv'
        Write-Text $path ("time`tpressure_pa`tpower_w`n" + ((0..9 | ForEach-Object { "2026-10-25 10:0$($_):00`t101,3$($_)0`t1,0$($_)0" }) -join "`n"))
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.Columns[1].Kind | Should -Be 'number'
        $layout.Columns[1].DecimalComma | Should -BeFalse
        $layout.Columns[2].DecimalComma | Should -BeFalse
    }

    It 'takes a semicolon file without a header, with decimal commas, as data' {
        $path = Join-Path $dir 'nohead.csv'
        Write-Text $path ((0..9 | ForEach-Object { "2026-10-25 10:0$($_):00;21,$_;0,5" }) -join "`n")
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.HeaderRecord | Should -Be -1
        $layout.Preview.Count | Should -Be 8
    }

    It 'reads Korean CP949 as Korean even when the sample ends inside a character' {
        $cp = [Text.Encoding]::GetEncoding(949)
        $bytes = $cp.GetBytes("$script:When,$script:Temp`n" + ((0..99 | ForEach-Object { "2026-10-25 $script:Pm 1:0$($_ % 10):00,21`n" }) -join ''))
        # The sample ends after the first byte of the first character of the afternoon word, whose second byte is 0xC0.
        $cut = 1
        while (-not ($bytes[$cut - 1] -eq 0xBF -and $bytes[$cut] -eq 0xC0)) { $cut++ }
        $name = $null
        [void][Hoodata.CsvAnalyzer]::DetectEncoding($bytes, $cut, [ref]$name)
        $name | Should -Be 'cp949'
    }

    It 'does not take degree signs in a Western code page for Korean' {
        if ([Text.Encoding]::Default.CodePage -eq 949) { Set-ItResult -Skipped -Because 'this PC uses the Korean code page'; return }
        $path = Join-Path $dir 'degrees.csv'
        $deg = [string][char]0xB0
        Write-Text $path ("time,Inside ($($deg)C),Outside ($($deg)F)`n2026-10-25 10:00:00,21,70`n2026-10-25 10:01:00,22,71") -CodePage 1252
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.EncodingName | Should -Not -Be 'cp949'
        $layout.Columns[1].Unit | Should -Be ($deg + 'C')
    }

    It 'reads a Western code page file in this PC''s code page, not as Korean' {
        if ([Text.Encoding]::Default.CodePage -eq 949) { Set-ItResult -Skipped -Because 'this PC uses the Korean code page'; return }
        $path = Join-Path $dir 'ansi.csv'
        Write-Text $path ("Zeit,Temperatur (" + [char]0xB0 + "C)`n2026-10-25 10:00:00,21`n2026-10-25 10:01:00,22") -CodePage ([Text.Encoding]::Default.CodePage)
        $layout = [Hoodata.CsvAnalyzer]::Analyze($path)
        $layout.EncodingName | Should -Be ('cp' + [Text.Encoding]::Default.CodePage)
        $layout.Columns[1].Unit | Should -Be ([string][char]0xB0 + 'C')
    }
}

Describe 'Engine' {
    BeforeAll {
        $dir = New-TestDir
        $engine = New-Object Hoodata.Engine($AppDir, $dir)
        $engine.Start(0)
        $cookies = New-Object Net.CookieContainer
        $write = @{ 'X-HOODATA' = '1' }
        [void](Invoke-Http $engine GET "/open?key=$($engine.Key)" -Cookies $cookies)
        function Add-Source($Kind, $Name, $Config) {
            $r = Invoke-Http $engine POST '/api/sources' @{ kind = $Kind; name = $Name; config = $Config } -Cookies $cookies -Headers $write
            ($r.Text | ConvertFrom-Json).id
        }
        function Get-Source([int]$Id) { ((Invoke-Http $engine GET '/api/sources' -Cookies $cookies).Text | ConvertFrom-Json) | Where-Object id -eq $Id }
        function Get-Channels([int]$Source) { @($engine.Channels() | Where-Object { $_.Info.Source -eq $Source }) }
    }
    AfterAll {
        $engine.Stop()
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }

    It 'gives the browser a session token, not the lasting key, under a name of its own' {
        $c = $cookies.GetCookies([Uri]"http://127.0.0.1:$($engine.Port)/")
        $c.Count | Should -Be 1
        $c[0].Name | Should -Match '^hoodata_[0-9a-f]{8}$'
        $c[0].Value | Should -Not -Be $engine.Key
    }

    It 'ignores a value dated far in the future from a running source, and says so' {
        $port = Get-FreePort
        $id = Add-Source 'http' 'devices' @{ port = $port }
        $key = (Get-Source $id).config.key
        $u = "http://127.0.0.1:$port/ingest"
        [void](Invoke-WebRequest -UseBasicParsing -Method Post -Uri $u -Headers @{ 'X-HOODATA-Key' = $key } -Body '{"x":1,"ts":3786912000}')
        Wait-Until { (Get-Source $id).error -eq 'future_times' } | Should -BeTrue
        [void](Invoke-WebRequest -UseBasicParsing -Method Post -Uri $u -Headers @{ 'X-HOODATA-Key' = $key } -Body '{"x":2}')
        Wait-Until { Get-Channels $id | Where-Object { $_.Info.Key -eq 'x' } } | Should -BeTrue
        $ch = Get-Channels $id | Where-Object { $_.Info.Key -eq 'x' }
        Wait-Until { $ch.Det.LastV -eq 2 } | Should -BeTrue
        $ch.Det.LastT | Should -BeLessThan ([Hoodata.Clock]::Now() + 60000)
        # Another device on the same source sending right does not hide it.
        Start-Sleep -Milliseconds 300
        (Get-Source $id).error | Should -Be 'future_times'
    }

    It 'does not count the same reading sent again as a sign of life' {
        $port = Get-FreePort
        $id = Add-Source 'http' 'repeater' @{ port = $port }
        $key = (Get-Source $id).config.key
        $u = "http://127.0.0.1:$port/ingest"
        $ts = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds() - 60
        foreach ($i in 0..9) {
            [void](Invoke-WebRequest -UseBasicParsing -Method Post -Uri $u -Headers @{ 'X-HOODATA-Key' = $key } -Body ('{"r":' + $i + ',"ts":' + ($ts + $i) + '}'))
            Start-Sleep -Milliseconds 200
        }
        Wait-Until { (Get-Channels $id).Count -eq 1 } | Should -BeTrue
        $ch = (Get-Channels $id)[0]
        Wait-Until { $ch.Det.Count -eq 10 } | Should -BeTrue
        $arrivals = Get-Private $ch.Det 'arrivals'
        $before = $arrivals.Count
        foreach ($i in 0..4) {
            [void](Invoke-WebRequest -UseBasicParsing -Method Post -Uri $u -Headers @{ 'X-HOODATA-Key' = $key } -Body ('{"r":9,"ts":' + ($ts + 9) + '}'))
            Start-Sleep -Milliseconds 200
        }
        Wait-Until { $ch.OutOfOrder -eq 5 } | Should -BeTrue
        $arrivals.Count | Should -Be $before
    }

    It 'caps the channels one source can make' {
        $port = Get-FreePort
        $id = Add-Source 'http' 'flood' @{ port = $port }
        $key = (Get-Source $id).config.key
        $body = '{' + ((0..1200 | ForEach-Object { "`"k$_`":1" }) -join ',') + '}'
        [void](Invoke-WebRequest -UseBasicParsing -Method Post -Uri "http://127.0.0.1:$port/ingest" -Headers @{ 'X-HOODATA-Key' = $key } -Body $body)
        Wait-Until { (Get-Channels $id).Count -ge 1000 } | Should -BeTrue
        Start-Sleep -Milliseconds 500
        (Get-Channels $id).Count | Should -Be ([Hoodata.Engine]::MaxChannelsPerSource)
        (Get-Source $id).error | Should -Be 'too_many_channels'
    }

    It 'says when a pattern period has no data' {
        $r = Invoke-Http $engine POST '/api/patterns/run' @{ from = 0; to = 1000 } -Cookies $cookies -Headers $write
        $r.Status | Should -Be 400
        $r.Text | Should -Match 'no_data_in_period'
    }

    It 'refuses an Excel 97-2003 file with a clear message' {
        [byte[]]$xls = 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0, 0, 0, 0
        $r = Invoke-Http $engine POST '/api/import?name=old.xls' $xls -Cookies $cookies -Headers $write
        $r.Status | Should -Be 400
        $r.Text | Should -Match 'xls_not_supported'
        @(Get-ChildItem (Join-Path $dir 'uploads')).Count | Should -Be 0
    }

    It 'dates rows without a time column so the last one is now' {
        $path = Join-Path $dir 'notime.csv'
        Write-Text $path ("a,b`n" + ((0..99 | ForEach-Object { "$_,$($_ * 2)" }) -join "`n"))
        $r = Invoke-Http $engine POST '/api/import?name=notime.csv' ([IO.File]::ReadAllBytes($path)) -Cookies $cookies -Headers $write
        $job = $r.Text | ConvertFrom-Json
        [void](Invoke-Http $engine POST "/api/import/$($job.id)/start" @{ time_column = -1; interval = 60 } -Cookies $cookies -Headers $write)
        Wait-Import ($engine.JobList() | Where-Object Id -eq $job.id)
        $ch = $engine.Channels() | Where-Object { $_.Info.Key -eq 'b' }
        $ch.Det.LastT | Should -BeLessOrEqual ([Hoodata.Clock]::Now())
        $ch.Det.LastT | Should -BeGreaterThan ([Hoodata.Clock]::Now() - 600000)
    }

    It 'follows a folder: waits for a header-only file, keeps files with other headers apart, rereads a replaced file' {
        $folder = Join-Path $dir 'logs'
        [void][IO.Directory]::CreateDirectory($folder)
        $t0 = [DateTime]::Now.AddHours(-2)
        $a = Join-Path $folder 'a.csv'
        [IO.File]::WriteAllText($a, "time,temp`n")
        $id = Add-Source 'folder' 'logs' @{ path = $folder; pattern = '*.csv' }
        Start-Sleep -Seconds 3
        [IO.File]::AppendAllText($a, ((0..9 | ForEach-Object { '{0},{1}' -f $t0.AddMinutes($_).ToString('yyyy-MM-dd HH:mm:ss'), (20 + $_) }) -join "`n") + "`n")
        Wait-Until { (Get-Channels $id | Where-Object { $_.Info.Key -eq 'temp' }).Det.Count -eq 10 } | Should -BeTrue
        [IO.File]::WriteAllText((Join-Path $folder 'b.csv'), "time,pressure`n" + ((0..4 | ForEach-Object { '{0},{1}' -f $t0.AddMinutes($_).ToString('yyyy-MM-dd HH:mm:ss'), (1000 + $_) }) -join "`n") + "`n")
        Wait-Until { Get-Channels $id | Where-Object { $_.Info.Key -eq 'pressure' } } | Should -BeTrue
        (Get-Channels $id | Where-Object { $_.Info.Key -eq 'temp' }).Det.LastV | Should -Be 29
        # a.csv is replaced by a new file of the same name that is already longer than the old one.
        [IO.File]::WriteAllText($a, "time,temp`n" + ((0..29 | ForEach-Object { '{0},{1}' -f $t0.AddMinutes(60 + $_).ToString('yyyy-MM-dd HH:mm:ss'), (50 + $_) }) -join "`n") + "`n")
        Wait-Until { (Get-Channels $id | Where-Object { $_.Info.Key -eq 'temp' }).Det.Count -eq 40 } | Should -BeTrue
    }

    It 'forgets a deleted folder source''s read positions' {
        $folder = Join-Path $dir 'logs2'
        [void][IO.Directory]::CreateDirectory($folder)
        [IO.File]::WriteAllText((Join-Path $folder 'log.csv'), "time,depth`n" + ((0..9 | ForEach-Object { '{0},{1}' -f [DateTime]::Now.AddMinutes(-20 + $_).ToString('yyyy-MM-dd HH:mm:ss'), $_ }) -join "`n") + "`n")
        $id = Add-Source 'folder' 'logs2' @{ path = $folder }
        Wait-Until { (Get-Channels $id | Where-Object { $_.Info.Key -eq 'depth' }).Det.Count -eq 10 } | Should -BeTrue
        [void](Invoke-Http $engine DELETE "/api/sources/$id" -Cookies $cookies -Headers $write)
        $engine.Store.GetMeta("folder:$id") | Should -BeNullOrEmpty
        $id2 = Add-Source 'folder' 'logs2 again' @{ path = $folder }
        $id2 | Should -Not -Be $id
        Wait-Until { (Get-Channels $id2 | Where-Object { $_.Info.Key -eq 'depth' }).Det.Count -eq 10 } | Should -BeTrue
    }

    It 'reads a file its logger keeps open for writing' {
        $folder = Join-Path $dir 'held'
        [void][IO.Directory]::CreateDirectory($folder)
        $t0 = [DateTime]::Now.AddMinutes(-30)
        $rows = { param($a, $b) -join ($a..$b | ForEach-Object { '{0},{1}' -f $t0.AddMinutes($_).ToString('yyyy-MM-dd HH:mm:ss'), $_ } | ForEach-Object { "$_`n" }) }
        $fs = New-Object IO.FileStream((Join-Path $folder 'today.csv'), 'Create', 'Write', 'Read')
        $w = New-Object IO.StreamWriter($fs)
        try {
            $w.Write("time,level`n" + (& $rows 0 4)); $w.Flush()
            $id = Add-Source 'folder' 'held' @{ path = $folder }
            Wait-Until { (Get-Channels $id | Where-Object { $_.Info.Key -eq 'level' }).Det.Count -eq 5 } | Should -BeTrue
            $w.Write((& $rows 5 9)); $w.Flush()
            Wait-Until { (Get-Channels $id | Where-Object { $_.Info.Key -eq 'level' }).Det.Count -eq 10 } | Should -BeTrue
        } finally { $w.Dispose() }
    }

    It 'rereads a file replaced while its source was paused, also behind a long header' {
        $folder = Join-Path $dir 'long'
        [void][IO.Directory]::CreateDirectory($folder)
        $t0 = [DateTime]::Now.AddHours(-3)
        $head = '# station 7 ' + ('calibrated sensor set ' * 20) + "`ntime,level`n"
        $file = Join-Path $folder 'current.csv'
        [IO.File]::WriteAllText($file, $head + (-join (0..9 | ForEach-Object { '{0},{1}' -f $t0.AddMinutes($_).ToString('yyyy-MM-dd HH:mm:ss'), $_ } | ForEach-Object { "$_`n" })))
        $id = Add-Source 'folder' 'long' @{ path = $folder }
        $level = { Get-Channels $id | Where-Object { $_.Info.Key -eq 'level' } }
        Wait-Until { (& $level).Det.Count -eq 10 } | Should -BeTrue
        [void](Invoke-Http $engine POST "/api/sources/$id" @{ enabled = $false } -Cookies $cookies -Headers $write)
        [IO.File]::WriteAllText($file, $head + (-join (0..19 | ForEach-Object { '{0},{1}' -f $t0.AddMinutes(60 + $_).ToString('yyyy-MM-dd HH:mm:ss'), (100 + $_) } | ForEach-Object { "$_`n" })))
        [void](Invoke-Http $engine POST "/api/sources/$id" @{ enabled = $true } -Cookies $cookies -Headers $write)
        Wait-Until { (& $level).Det.Count -eq 30 } | Should -BeTrue
    }

    It 'skips an MQTT message over 1 MB and stays connected' {
        $broker = New-Object Hoodata.Sim.MqttTestBroker('cam', 'pw')
        try {
            $broker.Publish('cam/snapshot', ('x' * 1100000), 0, $true)
            $id = Add-Source 'mqtt' 'big' @{ host = '127.0.0.1'; port = $broker.Port; user = 'cam'; password = 'pw'; topics = '#' }
            Wait-Until { $broker.Subscribed -gt 0 } | Should -BeTrue
            Start-Sleep -Milliseconds 500
            $broker.Publish('sensor/temp', '21.5', 0, $false)
            Wait-Until { Get-Channels $id | Where-Object { $_.Info.Key -eq 'sensor/temp' } } | Should -BeTrue
            (Get-Source $id).state | Should -Be 'running'
            ($engine.Sources() | Where-Object { $_.Info.Id -eq $id }).Impl.Skipped | Should -Be 1
        } finally { $broker.Dispose() }
    }

    It 'closes a connection that trickles its request body' {
        $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $engine.Port)
        $s = $c.GetStream()
        $head = [Text.Encoding]::ASCII.GetBytes("POST /api/settings HTTP/1.1`r`nHost: 127.0.0.1:$($engine.Port)`r`nContent-Length: 1000`r`n`r`n")
        $s.Write($head, 0, $head.Length)
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $answered = $false
        while (-not $answered -and $sw.Elapsed.TotalSeconds -lt 40) {
            try { $s.Write([byte[]]@(65), 0, 1) } catch { $answered = $true; break }
            Start-Sleep -Seconds 2
            # Readable: the answer came, or the connection was closed.
            if ($c.Client.Poll(0, [Net.Sockets.SelectMode]::SelectRead)) { $answered = $true }
        }
        $answered | Should -BeTrue
        $sw.Elapsed.TotalSeconds | Should -BeLessThan 30
        $c.Close()
    }

    It 'closes a connection that trickles its request' {
        $c = New-Object Net.Sockets.TcpClient('127.0.0.1', $engine.Port)
        $s = $c.GetStream()
        $s.Write([byte[]][char[]]'G', 0, 1)
        $s.ReadTimeout = 15000
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $buf = New-Object byte[] 512
        $n = try { $s.Read($buf, 0, 512) } catch { 0 }
        $sw.Elapsed.TotalSeconds | Should -BeLessThan 14
        $c.Close()
    }
}

Describe 'Launcher' {
    It 'leaves a folder named data that is not HOODATA''s with its own permissions' {
        $d = New-TestDir
        try {
            [IO.File]::WriteAllText((Join-Path $d 'logger.csv'), 'x')
            Test-PrivateDirectory $d | Should -BeFalse
            Protect-OwnDirectory $d
            Test-PrivateDirectory $d | Should -BeFalse
            [IO.File]::WriteAllText((Join-Path $d 'hoodata.db'), '')
            Protect-OwnDirectory $d
            Test-PrivateDirectory $d | Should -BeTrue
        } finally { Remove-Item -Recurse -Force $d -ErrorAction SilentlyContinue }
    }
}

Describe 'Patterns' {
    It 'tells a switch from a measurement that rests at one value' {
        $isSwitch = [Hoodata.PatternAnalyzer].GetMethod('IsSwitch', [Reflection.BindingFlags]'NonPublic, Static')
        function New-Set([double[]]$Values) {
            $h = [System.Collections.Generic.HashSet[double]]::new()
            foreach ($x in $Values) { [void]$h.Add($x) }
            , $h
        }
        $off = New-Set 0, 1
        $offOn = New-Set 0, 1
        # A pump read every 5 s: off or on, switching now and then.
        $isSwitch.Invoke($null, [object[]]@($off.psobject.BaseObject, $offOn.psobject.BaseObject, [int[]](100, 90))) | Should -BeTrue
        # A power meter read every 10 s: exactly 0 while off, many values while on.
        $zero = New-Set 0
        $many = New-Set 0, 37.2, 51.1, 44.8, 60.3
        $isSwitch.Invoke($null, [object[]]@($zero.psobject.BaseObject, $many.psobject.BaseObject, [int[]](100, 55))) | Should -BeFalse
    }

    It 'gives a measurement read several times a minute its high and low situations' {
        $dir = New-TestDir
        $engine = New-Object Hoodata.Engine($AppDir, (Join-Path $dir 'data'))
        try {
            $engine.Start(0)
            # Three days every 10 s: the soil sensor spikes every 30 minutes while the humidity is near its daily top,
            # and otherwise every 8 hours.
            $inv = [Globalization.CultureInfo]::InvariantCulture
            $rnd = New-Object Random 7
            $start = [DateTime]::new(2026, 9, 1, 0, 0, 0)
            $sb = New-Object Text.StringBuilder
            [void]$sb.Append("time,hum,soil`n")
            $lastSpike = -1e9
            for ($sec = 0; $sec -lt 3 * 86400; $sec += 10) {
                $sin = [Math]::Sin(2 * [Math]::PI * $sec / 86400.0)
                $soil = 30 + 0.2 * ($rnd.NextDouble() - 0.5)
                $every = if ($sin -gt 0.85) { 1800 } else { 8 * 3600 }
                if ($sec - $lastSpike -ge $every -and $sec -gt 3600) { $soil = 40; $lastSpike = $sec }
                [void]$sb.Append($start.AddSeconds($sec).ToString('yyyy-MM-dd HH:mm:ss', $inv) + ',' + (60 + 20 * $sin + 0.5 * ($rnd.NextDouble() - 0.5)).ToString('0.000', $inv) + ',' + $soil.ToString('0.000', $inv) + "`n")
            }
            $csv = Join-Path $dir 'every10s.csv'
            [IO.File]::WriteAllText($csv, $sb.ToString())
            $job = [Hoodata.ImportJob]::Prepare($csv, 'every10s.csv')
            $engine.AddJob($job)
            $job.Run($engine)
            $key = @{}
            $ids = New-Object 'System.Collections.Generic.List[int]'
            foreach ($c in $engine.Channels()) { $key[$c.Info.Id] = $c.Info.Key; if ($c.Info.Key -in 'hum', 'soil') { $ids.Add($c.Info.Id) } }
            $from = [Hoodata.Clock]::FromUtc($start.ToUniversalTime())
            $res = [Hoodata.PatternAnalyzer]::Run($engine.Store, $ids, $from, $from + 3L * 86400000)
            $rule = $res.Rules | Where-Object { $key[$_.Ch] -eq 'soil' -and $_.Kind -eq 'spike' -and $_.When.Count -eq 1 -and $_.When[0].Type -eq 'high' -and $key[$_.When[0].Ch] -eq 'hum' }
            $rule | Should -Not -BeNullOrEmpty
        } finally {
            $engine.Stop()
            Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
        }
    }
}
