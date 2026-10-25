# Pester 5 tests for the inputs: device message formats, HTTP, MQTT, Modbus TCP and RTU, Excel files and folders.
# Devices this PC does not have are stood in for by dev\sim\Simulators.cs; serial ports are not tested (none here).

BeforeAll { . (Join-Path $PSScriptRoot 'Helpers.ps1') }

Describe 'Device message formats' {
    It 'reads JSON, nested names and a timestamp in seconds' {
        $p = [Hoodata.Payload]::Parse('{"temp": 23.5, "env": {"hum": 61, "ok": true}, "ts": 1792900000}', $null)
        $p.Count | Should -Be 1
        ($p[0].Values | ForEach-Object Key) -join ',' | Should -Be 'temp,env.hum,env.ok'
        $p[0].Time | Should -Be 1792900000000
    }

    It 'reads name=value and the Arduino Serial Plotter format' {
        foreach ($line in 'temp=23.5,hum=61', 'temp:23.5 hum:61', ('temp:23.5' + [char]9 + 'hum:61')) {
            $v = ([Hoodata.Payload]::Parse($line, $null))[0].Values
            ($v | ForEach-Object Key) -join ',' | Should -Be 'temp,hum' -Because $line
            $v[1].Value | Should -Be 61
        }
    }

    It 'reads names in Korean' {
        $v = ([Hoodata.Payload]::Parse("$($script:Temp)=23.5", $null))[0].Values
        $v[0].Key | Should -Be $script:Temp
    }

    It 'reads the InfluxDB line protocol' {
        $p = [Hoodata.Payload]::Parse('air,room=lab temp=23.5,hum=61i 1792900000000000000', $null)
        ($p[0].Values | ForEach-Object Key) -join ',' | Should -Be 'air.lab.temp,air.lab.hum'
        $p[0].Time | Should -Be 1792900000000
    }

    It 'names bare numbers after the topic or device' {
        $r = [Hoodata.Payload]::ToReadings([Hoodata.Payload]::Parse('21.5', $null), 'farm/room1/temp', 5)
        $r[0].Key | Should -Be 'farm/room1/temp'
        $r[0].T | Should -Be 5
        $r = [Hoodata.Payload]::ToReadings([Hoodata.Payload]::Parse('1,2,3', $null), $null, 5)
        ($r | ForEach-Object Key) -join ',' | Should -Be 'value1,value2,value3'
        $r = [Hoodata.Payload]::ToReadings([Hoodata.Payload]::Parse('{"temp":1}', $null), 'sensors/a', 5)
        $r[0].Key | Should -Be 'sensors/a/temp'
    }

    It 'ignores lines that are not values' {
        [Hoodata.Payload]::Parse('# booting...', $null).Count | Should -Be 0
        [Hoodata.Payload]::Parse('ERROR sensor not found', $null).Count | Should -Be 0
    }
}

Describe 'Modbus' {
    It 'computes the CRC of the standard example' {
        [byte[]]$frame = 0x01, 0x03, 0x00, 0x00, 0x00, 0x0A
        '{0:X4}' -f [Hoodata.Modbus]::Crc($frame, 6) | Should -Be 'CDC5'
    }

    It 'reads an RTU device over a serial line and notices a bad CRC' {
        $a = $null; $b = $null
        [Hoodata.Sim.LineEnd]::Pair([ref]$a, [ref]$b)
        $dev = New-Object Hoodata.Sim.RtuDevice($b, 7)
        $dev.Memory.Holding[4] = 1234
        $dev.Memory.SetFloat(10, 3.25, $true)
        $t = New-Object Hoodata.ModbusRtuTransport($a)
        $item = New-Object Hoodata.ModbusItem
        $item.Address = 4; $item.Scale = 0.1
        $item.Decode($t.Read(7, 3, 4, 1)) | Should -Be 123.4
        $f = New-Object Hoodata.ModbusItem
        $f.Address = 10; $f.Type = 'float32'; $f.SwapWords = $true
        $f.Decode($t.Read(7, 3, 10, 2)) | Should -Be 3.25
        { $t.Read(7, 3, 500, 1) } | Should -Throw '*illegal address*'
        $dev.CorruptNext = $true
        { $t.Read(7, 3, 4, 1) } | Should -Throw '*CRC*'
    }
}

Describe 'Excel files' {
    It 'turns a workbook Excel wrote into CSV, with dates, units, Korean headers and booleans' {
        $dir = New-TestDir
        try {
            $csv = Join-Path $dir 'sheet.csv'
            [Hoodata.Xlsx]::ToCsv((Join-Path $PSScriptRoot 'fixtures\excel-sample.xlsx'), $csv) | Should -Be 11
            $layout = [Hoodata.CsvAnalyzer]::Analyze($csv)
            $layout.TimeColumn | Should -Be 0
            $layout.Columns[1].Unit | Should -Be ([string][char]0x2103)
            $layout.Columns[2].Unit | Should -Be '%'
            $layout.Columns[3].Kind | Should -Be 'number'
            $layout.Preview[0][0] | Should -Be '2026-03-01 09:00:00'
            $layout.Preview[3][4] | Should -Be 'door open, checked'
            $layout.Preview[1][3] | Should -Be '0'
        } finally { Remove-Item -Recurse -Force $dir }
    }

    It 'tells date formats from number formats' {
        [Hoodata.Xlsx]::IsDateFormat('yyyy\-mm\-dd\ hh:mm:ss') | Should -BeTrue
        [Hoodata.Xlsx]::IsDateFormat('[$-412]yyyy"' + [char]0xB144 + '" m"' + [char]0xC6D4 + '"') | Should -BeTrue
        [Hoodata.Xlsx]::IsDateFormat('0.00') | Should -BeFalse
        [Hoodata.Xlsx]::IsDateFormat('#,##0 "m"') | Should -BeFalse
        [Hoodata.Xlsx]::IsDateFormat('General') | Should -BeFalse
    }
}

Describe 'Inputs' {
    BeforeAll {
        $dir = New-TestDir
        $engine = New-Object Hoodata.Engine($AppDir, $dir)
        $engine.Start(0)
        $cookies = New-Object Net.CookieContainer
        $write = @{ 'X-HOODATA' = '1' }
        [void](Invoke-Http $engine GET "/open?key=$($engine.Key)" -Cookies $cookies)
        function Add-Source($Kind, $Name, $Config) {
            $r = Invoke-Http $engine POST '/api/sources' @{ kind = $Kind; name = $Name; config = $Config } -Cookies $cookies -Headers $write
            $r.Status | Should -Be 200 -Because $r.Text
            ($r.Text | ConvertFrom-Json).id
        }
        function Get-Channel([string]$Key) { $engine.Channels() | Where-Object { $_.Info.Key -eq $Key } }
        function Get-Source([int]$Id) { ((Invoke-Http $engine GET '/api/sources' -Cookies $cookies).Text | ConvertFrom-Json) | Where-Object id -eq $Id }
    }
    AfterAll {
        $engine.Stop()
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    }

    It 'receives values over HTTP with the source key, on its own port' {
        $port = Get-FreePort
        $id = Add-Source 'http' 'devices' @{ port = $port; lan = $false }
        $key = (Get-Source $id).config.key
        $key.Length | Should -Be 32
        $url = "http://127.0.0.1:$port"
        $ok = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$url/ingest/room1" -Headers @{ 'X-HOODATA-Key' = $key } -Body '{"temp":21.5,"hum":40}'
        $ok.StatusCode | Should -Be 200
        [void](Invoke-WebRequest -UseBasicParsing -Uri "$url/ingest?key=$key&level=3.5")
        { Invoke-WebRequest -UseBasicParsing -Method Post -Uri "$url/ingest" -Body 'x=1' -Headers @{ 'X-HOODATA-Key' = 'wrong' } } | Should -Throw '*401*'
        Wait-Until { Get-Channel 'room1/temp' } | Should -BeTrue
        (Get-Channel 'room1/temp').Det.LastV | Should -Be 21.5
        Wait-Until { Get-Channel 'level' } | Should -BeTrue
        # The dashboard's port does not take values.
        (Invoke-Http $engine POST '/ingest' -Body ([Text.Encoding]::UTF8.GetBytes('x=1'))).Status | Should -Be 404
    }

    It 'subscribes to an MQTT broker, with the password kept encrypted' {
        $broker = New-Object Hoodata.Sim.MqttTestBroker('farm', 's3cret')
        try {
            $broker.Publish('farm/old', '99', 0, $true)
            $id = Add-Source 'mqtt' 'broker' @{ host = '127.0.0.1'; port = $broker.Port; user = 'farm'; password = 's3cret'; topics = 'farm/#'; qos = 1 }
            Wait-Until { $broker.Subscribed -gt 0 } | Should -BeTrue
            $broker.Publish('farm/room1/temp', '21.5', 1, $false)
            $broker.Publish('farm/room2', '{"temp":20,"hum":55}', 0, $false)
            Wait-Until { (Get-Channel 'farm/room1/temp') -and (Get-Channel 'farm/room2/hum') } | Should -BeTrue
            (Get-Channel 'farm/room2/hum').Det.LastV | Should -Be 55
            Get-Channel 'farm/old' | Should -BeNullOrEmpty -Because 'a retained message was measured earlier'
            Wait-Until { $broker.Pubacks -ge 1 } | Should -BeTrue
            $src = Get-Source $id
            $src.state | Should -Be 'running'
            $src.config.has_password | Should -BeTrue
            $src.config.PSObject.Properties.Name | Should -Not -Contain 'password_enc'
            $stored = ($engine.Sources() | Where-Object { $_.Info.Id -eq $id }).Info.Config
            $stored | Should -Not -Match 's3cret'
            $stored | Should -Match 'password_enc'
        } finally { $broker.Dispose() }
    }

    It 'says why when the broker refuses the password' {
        $broker = New-Object Hoodata.Sim.MqttTestBroker('farm', 's3cret')
        try {
            $id = Add-Source 'mqtt' 'wrong' @{ host = '127.0.0.1'; port = $broker.Port; user = 'farm'; password = 'nope'; topics = '#' }
            Wait-Until { (Get-Source $id).state -eq 'error' } | Should -BeTrue
            (Get-Source $id).error | Should -Match 'user name or password'
        } finally { $broker.Dispose() }
    }

    It 'reads a Modbus TCP device by its register map' {
        $srv = New-Object Hoodata.Sim.ModbusTestServer
        try {
            $srv.Memory.Holding[0] = 235
            $srv.Memory.Holding[1] = 65531
            $srv.Memory.SetFloat(10, 3.25, $false)
            $srv.Memory.Input[5] = 1234
            $srv.Memory.Coils[3] = $true
            $regs = @(
                @{ name = 'temp'; fn = 'holding'; address = 0; type = 'int16'; scale = 0.1; unit = 'C' },
                @{ name = 'offset'; fn = 'holding'; address = 1; type = 'int16' },
                @{ name = 'flow'; fn = 'holding'; address = 10; type = 'float32'; order = 'big' },
                @{ name = 'count'; fn = 'input'; address = 5; type = 'uint16' },
                @{ name = 'pump'; fn = 'coil'; address = 3 },
                @{ name = 'missing'; fn = 'holding'; address = 500 })
            $id = Add-Source 'modbus' 'plc' @{ host = '127.0.0.1'; port = $srv.Port; unit = 1; interval = 0.5; registers = $regs }
            Wait-Until { Get-Channel 'pump' } | Should -BeTrue
            (Get-Channel 'temp').Det.LastV | Should -Be 23.5
            (Get-Channel 'temp').Info.Unit | Should -Be 'C'
            (Get-Channel 'offset').Det.LastV | Should -Be -5
            (Get-Channel 'flow').Det.LastV | Should -Be 3.25
            (Get-Channel 'count').Det.LastV | Should -Be 1234
            (Get-Channel 'pump').Det.LastV | Should -Be 1
            Get-Channel 'missing' | Should -BeNullOrEmpty
            (Get-Source $id).error | Should -Match 'missing: .*illegal address'
        } finally { $srv.Dispose() }
    }

    It 'follows a folder a logger writes to, line by line and file by file' {
        $folder = Join-Path $dir 'logger'
        [void][IO.Directory]::CreateDirectory($folder)
        $f1 = Join-Path $folder 'day1.csv'
        $t0 = [DateTime]::new(2026, 10, 1, 8, 0, 0)
        $line = { param($i) '{0},{1}' -f $t0.AddMinutes($i).ToString('yyyy-MM-dd HH:mm:ss'), (10 + $i) }
        [IO.File]::WriteAllText($f1, "time,depth`n" + ((0..4 | ForEach-Object { & $line $_ }) -join "`n") + "`n")
        $id = Add-Source 'folder' 'logger' @{ path = $folder; pattern = '*.csv' }
        Wait-Until { (Get-Channel 'depth') -and (Get-Channel 'depth').Det.Count -eq 5 } | Should -BeTrue
        # A line still being written (no line end yet) waits.
        [IO.File]::AppendAllText($f1, ((5..6 | ForEach-Object { & $line $_ }) -join "`n") + "`n" + (& $line 7).Substring(0, 10))
        Wait-Until { (Get-Channel 'depth').Det.Count -eq 7 } | Should -BeTrue
        Start-Sleep -Seconds 3
        (Get-Channel 'depth').Det.Count | Should -Be 7
        [IO.File]::AppendAllText($f1, (& $line 7).Substring(10) + "`n")
        Wait-Until { (Get-Channel 'depth').Det.Count -eq 8 } | Should -BeTrue
        [IO.File]::WriteAllText((Join-Path $folder 'day2.csv'), "time,depth`n" + ((8..9 | ForEach-Object { & $line $_ }) -join "`n") + "`n")
        Wait-Until { (Get-Channel 'depth').Det.Count -eq 10 } | Should -BeTrue
        (Get-Channel 'depth').Det.LastV | Should -Be 19
        $engine.Store.GetMeta("folder:$id") | Should -Match 'day2.csv'
    }

    It 'lists serial ports (none on a PC without them)' {
        { [Hoodata.SerialSource]::Ports() } | Should -Not -Throw
        (Invoke-Http $engine GET '/api/serial-ports' -Cookies $cookies).Status | Should -Be 200
    }
}
