param(
    [Parameter(Mandatory = $true)][string]$RegressionReport,
    [ValidateSet('baseline', 'editor', 'keil', 'sources', 'environment', 'peripheral', 'fault')][string[]]$MaintenanceAreas = @('baseline'),
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Maintenance-Gates.ps1')
$source = Split-Path -Parent $PSScriptRoot
$reportPath = [IO.Path]::GetFullPath($RegressionReport)
$policy = Read-StudioXMaintenancePolicy
$areas = Get-StudioXMaintenanceAreas $policy $MaintenanceAreas
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
$snapshot = Get-StudioXMaintenanceSourceSnapshot $source
$commit = (& git -C $source rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Source commit unavailable.' }
$result = Test-StudioXMaintenanceEvidence $policy $areas $report ([IO.Path]::GetDirectoryName($reportPath)) $snapshot $commit
if ($OutputFile)
{
    if (Test-Path -LiteralPath $OutputFile) { throw 'Use a new maintenance audit output file.' }
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputFile), ($result | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
}
if (!$result.passed) { throw ($result.errors -join [Environment]::NewLine) }
Write-Output "PASS maintenance: $($areas -join ', '); source $($snapshot.sha256)"
$global:LASTEXITCODE = 0
