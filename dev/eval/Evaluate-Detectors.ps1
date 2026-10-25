# Runs the detectors over the synthetic scenarios of Synthetic.cs and prints, per scenario and kind, how many of the
# anomalies put in were found (recall) and how many findings were not real (false positives).
#   .\dev\eval\Evaluate-Detectors.ps1 [-Seed 7] [-Sensitivity normal|low|high|all] [-Csv out.csv]
param([int]$Seed = 7, [string]$Sensitivity = 'all', [string]$Csv)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
. (Join-Path $root 'app\HOODATA.Setup.ps1')
if (-not ('Hoodata.Eval.Synthetic' -as [type])) {
    Set-CompilerTemp
    $files = @(Get-ChildItem (Join-Path $root 'app\engine') -Filter *.cs | ForEach-Object { $_.FullName }) + (Join-Path $PSScriptRoot 'Synthetic.cs')
    Add-Type -Path $files -ReferencedAssemblies System, System.Core
}

$levels = if ($Sensitivity -eq 'all') { 'low', 'normal', 'high' } else { @($Sensitivity) }
$rows = foreach ($scenario in [Hoodata.Eval.Synthetic]::All($Seed)) {
    foreach ($level in $levels) {
        $events = [Hoodata.Eval.Evaluator]::Run($scenario, [Hoodata.DetectSettings]::For($level))
        foreach ($r in [Hoodata.Eval.Evaluator]::Score($scenario, $events)) {
            [pscustomobject]@{
                Scenario    = $r.Scenario
                Sensitivity = $level
                Kind        = $r.Kind
                Truths      = $r.Truths
                Found       = $r.Found
                Recall      = if ($r.Truths) { [Math]::Round($r.Found / $r.Truths, 2) } else { $null }
                Detections  = $r.Detections
                FalsePos    = $r.FalsePositives
                FalsePerDay = [Math]::Round($r.FalsePositives / [Math]::Max($r.Days, 1e-9), 2)
            }
        }
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 200
if ($Csv) { $rows | Export-Csv -NoTypeInformation -Encoding UTF8 $Csv }
