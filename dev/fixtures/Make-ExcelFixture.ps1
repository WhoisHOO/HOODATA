# Makes tests\fixtures\excel-sample.xlsx with Microsoft Excel itself (through COM, without showing a window), so the
# XLSX reader is tested on a file Excel wrote, not one this project wrote. Needs Excel; the tests only need the file.
#   .\dev\fixtures\Make-ExcelFixture.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out = Join-Path $root 'tests\fixtures\excel-sample.xlsx'
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $out))

# Korean headers from code points, so this file stays ASCII.
function K([int[]]$Codes) { -join ($Codes | ForEach-Object { [char]$_ }) }
$headers = @(
    (K 0xCE21, 0xC815, 0x20, 0xC2DC, 0xAC01),            # measured at
    ((K 0xC628, 0xB3C4) + '(' + [char]0x2103 + ')'),       # temperature (deg C)
    'Humidity [%]',
    (K 0xD38C, 0xD504),                                   # pump
    'Note')

$excel = New-Object -ComObject Excel.Application
$excel.Visible = $false
$excel.DisplayAlerts = $false
try {
    $book = $excel.Workbooks.Add()
    $sheet = $book.Worksheets.Item(1)
    # Cells by address: setting values through Cells.Item(row, column) fails in Windows PowerShell's COM binder.
    $columns = 'A', 'B', 'C', 'D', 'E'
    for ($c = 0; $c -lt $headers.Count; $c++) { $sheet.Range("$($columns[$c])1").Value2 = [string]$headers[$c] }
    $start = [DateTime]::new(2026, 3, 1, 9, 0, 0)
    for ($i = 0; $i -lt 10; $i++) {
        $row = $i + 2
        $sheet.Range("A$row").Value2 = [double]$start.AddMinutes(10 * $i).ToOADate()
        $sheet.Range("B$row").Value2 = [double](20.5 + $i / 4)
        $sheet.Range("C$row").Value2 = [double](40 + $i)
        $sheet.Range("D$row").Value2 = [bool]($i % 2 -eq 0)
        if ($i -eq 3) { $sheet.Range("E$row").Value2 = 'door open, checked' }
    }
    $sheet.Range('A2:A11').NumberFormat = 'yyyy-mm-dd hh:mm:ss'
    if (Test-Path $out) { Remove-Item $out }
    $book.SaveAs($out, 51)   # 51: xlOpenXMLWorkbook (.xlsx)
    $book.Close($false)
} finally {
    $excel.Quit()
    # Excel stays in memory until every COM object is released.
    foreach ($o in $sheet, $book, $excel) { if ($o) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($o) } }
    [GC]::Collect()
    [GC]::WaitForPendingFinalizers()
}
"Wrote $out"
