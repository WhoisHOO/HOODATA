# Makes the Excel test files with Microsoft Excel itself (through COM, without showing a window), so the XLSX reader
# is tested on files Excel wrote, not ones this project wrote. Needs Excel; the tests only need the files.
#   tests\fixtures\excel-sample.xlsx      date-time column, Korean headers, units, booleans, a sparse column
#   tests\fixtures\excel-date-time.xlsx   separate date and time columns in Excel's built-in formats
#   .\dev\fixtures\Make-ExcelFixture.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dir = Join-Path $root 'tests\fixtures'
[void][IO.Directory]::CreateDirectory($dir)

# Korean headers from code points, so this file stays ASCII.
function K([int[]]$Codes) { -join ($Codes | ForEach-Object { [char]$_ }) }

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
$book = $null
$sheet = $null

# Cells by address: setting values through Cells.Item(row, column) fails in Windows PowerShell's COM binder.
function Save-Book([string]$Name, [scriptblock]$Fill) {
    $script:book = $excel.Workbooks.Add()
    $script:sheet = $script:book.Worksheets.Item(1)
    & $Fill $script:sheet
    $out = Join-Path $dir $Name
    if (Test-Path $out) { Remove-Item $out }
    $script:book.SaveAs($out, 51)   # 51: xlOpenXMLWorkbook (.xlsx)
    $script:book.Close($false)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($script:sheet)
    [void][Runtime.InteropServices.Marshal]::ReleaseComObject($script:book)
    $script:sheet = $null
    $script:book = $null
    "Wrote $out"
}

try {
    Save-Book 'excel-sample.xlsx' {
        param($s)
        $headers = @(
            (K 0xCE21, 0xC815, 0x20, 0xC2DC, 0xAC01),            # measured at
            ((K 0xC628, 0xB3C4) + '(' + [char]0x2103 + ')'),       # temperature (deg C)
            'Humidity [%]',
            (K 0xD38C, 0xD504),                                   # pump
            'Note')
        $columns = 'A', 'B', 'C', 'D', 'E'
        for ($c = 0; $c -lt $headers.Count; $c++) { $s.Range("$($columns[$c])1").Value2 = [string]$headers[$c] }
        $start = [DateTime]::new(2026, 3, 1, 9, 0, 0)
        for ($i = 0; $i -lt 10; $i++) {
            $row = $i + 2
            $s.Range("A$row").Value2 = [double]$start.AddMinutes(10 * $i).ToOADate()
            $s.Range("B$row").Value2 = [double](20.5 + $i / 4)
            $s.Range("C$row").Value2 = [double](40 + $i)
            $s.Range("D$row").Value2 = [bool]($i % 2 -eq 0)
            if ($i -eq 3) { $s.Range("E$row").Value2 = 'door open, checked' }
        }
        $s.Range('A2:A11').NumberFormat = 'yyyy-mm-dd hh:mm:ss'
    }
    Save-Book 'excel-date-time.xlsx' {
        param($s)
        $s.Range('A1').Value2 = 'Date'
        $s.Range('B1').Value2 = 'Time'
        $s.Range('C1').Value2 = 'Temp'
        $start = [DateTime]::new(2026, 3, 1, 9, 0, 0)
        for ($i = 0; $i -lt 10; $i++) {
            $row = $i + 2
            $t = $start.AddMinutes(10 * $i)
            $s.Range("A$row").Value2 = [double]$t.Date.ToOADate()
            $s.Range("B$row").Value2 = [double]($t.TimeOfDay.TotalDays)
            $s.Range("C$row").Value2 = [double](20 + $i)
        }
        $s.Range('A2:A11').NumberFormat = 'm/d/yyyy'   # Excel stores it as its built-in format 14
        $s.Range('B2:B11').NumberFormat = 'h:mm:ss'    # built-in 21
    }
} finally {
    $excel.Quit()
    # Excel stays in memory until every COM object is released.
    foreach ($o in $sheet, $book, $excel) { if ($o) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($o) } }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
