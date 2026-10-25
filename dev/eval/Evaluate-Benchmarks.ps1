# Measures the detectors on two public benchmarks (see Benchmarks.cs for how they are scored). The data is
# downloaded once into -Cache and is not part of the repository.
#   .\dev\eval\Evaluate-Benchmarks.ps1 [-Cache <dir>] [-Sensitivity normal]
param([string]$Cache = (Join-Path ([IO.Path]::GetTempPath()) 'hoodata-benchmarks'), [string]$Sensitivity = 'normal')

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'app\HOODATA.Setup.ps1')
Import-HoodataEngine (Join-Path $root 'app') -Extra (Join-Path $PSScriptRoot 'Synthetic.cs'), (Join-Path $PSScriptRoot 'Benchmarks.cs')
$cfg = [Hoodata.DetectSettings]::For($Sensitivity)

# NAB, pinned to a commit so the numbers can be reproduced.
$nabCommit = 'ea702d75cc2258d9d7dd35ca8e5e2539d71f3140'
$nab = Join-Path $Cache 'nab'
$labels = Join-Path $nab 'combined_windows.json'
if (-not (Test-Path $labels)) {
    [void][IO.Directory]::CreateDirectory($nab)
    Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/numenta/NAB/$nabCommit/labels/combined_windows.json" -OutFile $labels
}
$windows = Get-Content -Raw $labels | ConvertFrom-Json
$nabRows = foreach ($p in $windows.PSObject.Properties) {
    $file = Join-Path $nab ('data\' + $p.Name.Replace('/', '\'))
    if (-not (Test-Path $file)) {
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $file))
        Invoke-WebRequest -UseBasicParsing "https://raw.githubusercontent.com/numenta/NAB/$nabCommit/data/$($p.Name)" -OutFile $file
    }
    $list = New-Object 'System.Collections.Generic.List[string[]]'
    foreach ($w in $p.Value) { $list.Add([string[]]@($w[0], $w[1])) }
    [Hoodata.Eval.Benchmarks]::Nab($file, $p.Name.Split('/')[0], $list, $cfg)
}
'NAB, by category:'
$nabRows | Group-Object Category | ForEach-Object {
    $g = $_.Group
    [pscustomobject]@{
        Category       = $_.Name
        Files          = $g.Count
        Points         = ($g | Measure-Object Points -Sum).Sum
        Windows        = ($g | Measure-Object Windows -Sum).Sum
        Found          = ($g | Measure-Object Found -Sum).Sum
        FalsePositives = ($g | Measure-Object FalsePositives -Sum).Sum
    }
} | Format-Table -AutoSize | Out-String -Width 160
$w = ($nabRows | Measure-Object Windows -Sum).Sum; $f = ($nabRows | Measure-Object Found -Sum).Sum
$fp = ($nabRows | Measure-Object FalsePositives -Sum).Sum; $pts = ($nabRows | Measure-Object Points -Sum).Sum
'NAB total: {0} of {1} windows found ({2:P0}); {3} false positives in {4} points ({5:N2} per 1000)' -f $f, $w, ($f / $w), $fp, $pts, (1000 * $fp / $pts)

# UCR: the archive as published in 2021.
$ucr = Join-Path $Cache 'ucr'
if (-not (Test-Path $ucr) -or -not (Get-ChildItem $ucr -Recurse -Filter '001_UCR_Anomaly_*.txt')) {
    [void][IO.Directory]::CreateDirectory($ucr)
    $zip = Join-Path $ucr 'UCR_TimeSeriesAnomalyDatasets2021.zip'
    Invoke-WebRequest -UseBasicParsing 'https://www.cs.ucr.edu/~eamonn/time_series_data_2018/UCR_TimeSeriesAnomalyDatasets2021.zip' -OutFile $zip
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $ucr)
    Remove-Item $zip   # 184 MB; the extracted files are what is used
}
$files = Get-ChildItem $ucr -Recurse -Filter '*_UCR_Anomaly_*.txt' | Where-Object { $_.DirectoryName -like '*UCR_Anomaly_FullData*' }
$ucrRows = foreach ($file in $files) { [Hoodata.Eval.Benchmarks]::Ucr($file.FullName, $cfg) }
$right = @($ucrRows | Where-Object Right).Count
$inside = @($ucrRows | Where-Object AnyInside).Count
''
'UCR: {0} of {1} series right ({2:P0}) by the strongest anomaly; an anomaly near the labelled one in {3} ({4:P0})' -f $right, $ucrRows.Count, ($right / $ucrRows.Count), $inside, ($inside / $ucrRows.Count)
'UCR: anomalies reported after the training part, median per series: {0}' -f (($ucrRows | Sort-Object TestEvents)[[int]($ucrRows.Count / 2)].TestEvents)
'UCR right answers by kind:'
$ucrRows | Where-Object Right | Group-Object TopKind | Select-Object Name, Count | Format-Table -AutoSize | Out-String
