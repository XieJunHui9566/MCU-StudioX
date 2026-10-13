param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Maintenance-Gates.ps1')
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new gate validation directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$source = Join-Path $output 'source'
[IO.Directory]::CreateDirectory($source) | Out-Null
& git -C $source init --quiet
if ($LASTEXITCODE -ne 0) { throw 'Cannot initialize isolated source fixture.' }
[IO.File]::WriteAllText((Join-Path $source 'main.cs'), 'class Original {}')
[IO.File]::WriteAllText((Join-Path $source '.gitignore'), 'ignored/' + [Environment]::NewLine)
$policy = Read-StudioXMaintenancePolicy
$snapshot = Get-StudioXMaintenanceSourceSnapshot $source
$evidence = Join-Path $output 'evidence'
[IO.Directory]::CreateDirectory($evidence) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Value, [string]$Message)
{
    if (!$Value) { throw $Message }
    $checks.Add($Message)
    Write-Output "PASS $Message"
}
function New-Report([string[]]$Areas)
{
    $records = @($policy.baselineGates) + @($policy.profiles | Where-Object { $_.id -cin $Areas } | ForEach-Object { $_.gates }) | Select-Object -Unique
    $inputs = @{}
    foreach ($profile in $policy.profiles | Where-Object { $_.id -cin $Areas })
    {
        foreach ($name in $profile.requiredInputs)
        {
            $inputPath = Join-Path $output ('input-' + $name)
            if ($policy.inputs.$name -eq 'file') { [IO.File]::WriteAllText($inputPath, '{}') }
            else { [IO.Directory]::CreateDirectory($inputPath) | Out-Null }
            $inputs[$name] = $inputPath
        }
        foreach ($summary in $profile.reports)
        {
            $path = Join-Path $evidence $summary.path
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
            $data = if (Test-Path -LiteralPath $path) { Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable } else { @{} }
            foreach ($property in $summary.expected.PSObject.Properties) { $data[$property.Name] = $property.Value }
            $data | ConvertTo-Json | Set-Content -LiteralPath $path
        }
    }
    $results = @($records | ForEach-Object {
        [IO.File]::WriteAllText((Join-Path $evidence ($_ + '.log')), 'original fixture diagnostic')
        @{name=$_; passed=$true; log=$_ + '.log'}
    })
    $inputFiles = @(Get-StudioXMaintenanceInputFiles $policy $inputs)
    return @{formatVersion=1; sourceCommit='fixture-commit'; sourceDirty=$true; hardware=$false; downloadedSdk=$false; passed=$true;
        results=$results; maintenanceAreas=$Areas; maintenanceInputs=$inputs; sourceSnapshot=@{before=$snapshot; after=$snapshot}; inputFileSnapshot=@{before=$inputFiles; after=$inputFiles}}
}
function Evaluate([object]$Report, [string[]]$Areas = @('baseline'))
{
    Test-StudioXMaintenanceEvidence $policy $Areas $Report $evidence (Get-StudioXMaintenanceSourceSnapshot $source) 'fixture-commit'
}
$report = New-Report @('baseline')
Check (Evaluate $report).passed 'all named baseline gates and original evidence pass for the same dirty source snapshot'
$report.results = @($report.results | Where-Object { $_.name -ne 'project-file-synchronization' })
Check (!(Evaluate $report).passed) 'missing required gate cannot be replaced by a larger arbitrary check count'
$report = New-Report @('baseline')
$report.results[0].passed = $false
Check (!(Evaluate $report).passed) 'a failed gate overrides the top-level passed flag'
$report = New-Report @('baseline')
$report.results += $report.results[0]
Check (!(Evaluate $report).passed) 'duplicate passing gate records are rejected'
$report = New-Report @('baseline')
Remove-Item -LiteralPath (Join-Path $evidence 'build.log')
Check (!(Evaluate $report).passed) 'missing original log blocks a successful maintenance claim'
$report = New-Report @('baseline')
$report.results[0].log = '../outside.log'
Check (!(Evaluate $report).passed) 'evidence traversal cannot stand in for a captured gate log'
$report = New-Report @('baseline')
$summary = Join-Path $evidence 'project-synchronization/result.json'
'{"success":"true"}' | Set-Content -LiteralPath $summary
Check (!(Evaluate $report).passed) 'string true is not accepted as a successful result'
$report = New-Report @('baseline')
$report.sourceSnapshot = $null
Check (!(Evaluate $report).passed) 'reports without a content snapshot remain historical evidence'
$report = New-Report @('baseline')
[IO.File]::WriteAllText((Join-Path $source 'main.cs'), 'class Changed {}')
Check (!(Evaluate $report).passed) 'editing source invalidates evidence even though commit and dirty state are unchanged'
[IO.File]::WriteAllText((Join-Path $source 'main.cs'), 'class Original {}')
$report = New-Report @('baseline')
$report.sourceSnapshot.after = @{sha256='changed during validation'}
Check (!(Evaluate $report).passed) 'source mutation during validation invalidates all completed gates'
$ignored = Join-Path $source 'ignored'
[IO.Directory]::CreateDirectory($ignored) | Out-Null
[IO.File]::WriteAllText((Join-Path $ignored 'build.log'), 'generated output')
Check ((Get-StudioXMaintenanceSourceSnapshot $source).sha256 -eq $snapshot.sha256) 'ignored build output does not change the source snapshot'
$report = New-Report @('baseline')
$report.sourceCommit = 'different-commit'
Check (!(Evaluate $report).passed) 'a different commit cannot reuse passing evidence'
$report = New-Report @('baseline', 'editor')
Check (Evaluate $report @('baseline', 'editor')).passed 'declared editor area requires real-language gates and a passed language summary'
@{success=$true; language='not_requested'} | ConvertTo-Json | Set-Content -LiteralPath $summary
Check (!(Evaluate $report @('baseline', 'editor')).passed) 'not-requested language validation cannot satisfy the editor area'
$report = New-Report @('baseline', 'keil')
@{success=$true; realCompilation='not_requested'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'keil-workflow/result.json')
Check (!(Evaluate $report @('baseline', 'keil')).passed) 'offline migration success cannot satisfy real Keil compilation'
$report = New-Report @('baseline', 'keil')
[IO.File]::WriteAllText($report.maintenanceInputs.KeilValidationInputs, '{"changed":"input"}')
Check (!(Evaluate $report @('baseline', 'keil')).passed) 'changing an ignored external input plan invalidates evidence without a source change'
$report = New-Report @('baseline', 'sources')
Check (Evaluate $report @('baseline', 'sources')).passed 'source registration area requires native and IDF real compilation evidence'
@{success=$true; realCompilation='not_requested'} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'source-registration/result.json')
Check (!(Evaluate $report @('baseline', 'sources')).passed) 'offline source preview cannot stand in for real compilation list validation'
$missingSourcesRejected = $false
$blockedSourcesOutput = Join-Path $output 'missing-source-inputs-must-not-start'
try { & (Join-Path $PSScriptRoot 'Test-Regression.ps1') -OutputDirectory $blockedSourcesOutput -MaintenanceAreas sources }
catch { $missingSourcesRejected = $_.Exception.Message -match 'requires -SourceRegistrationInputs' }
Check ($missingSourcesRejected -and !(Test-Path -LiteralPath $blockedSourcesOutput)) 'source maintenance area rejects missing build inputs before starting'
$report = New-Report @('baseline', 'editor')
$report.maintenanceAreas = @('baseline')
Check (!(Evaluate $report @('baseline', 'editor')).passed) 'areas cannot be added retrospectively to a passing report'
$report = New-Report @('baseline', 'environment', 'peripheral', 'fault')
Check (Evaluate $report @('baseline', 'environment', 'peripheral', 'fault')).passed 'existing environment, peripheral and saved-dump gates compose with the baseline'
$report.maintenanceInputs.Remove('CoreDumpFixtures')
Check (!(Evaluate $report @('baseline', 'environment', 'peripheral', 'fault')).passed) 'saved-dump profile requires explicit fixture inputs'
$rejected = $false
$blockedOutput = Join-Path $output 'missing-runtime-must-not-start'
try { & (Join-Path $PSScriptRoot 'Test-Regression.ps1') -OutputDirectory $blockedOutput -MaintenanceAreas editor }
catch { $rejected = $_.Exception.Message -match 'requires -LanguageRuntime' }
Check ($rejected -and !(Test-Path -LiteralPath $blockedOutput)) 'missing mandatory runtime is rejected before a build or output directory starts'
$gateRuntimeBefore = $env:StudioXRuntimeAssetsDirectory
$env:StudioXRuntimeAssetsDirectory = 'existing-maintenance-runtime'
$earlyRejected = $false
$earlyOutput = Join-Path $output 'invalid-optional-input'
try
{
    try { & (Join-Path $PSScriptRoot 'Test-Regression.ps1') -OutputDirectory $earlyOutput -ProjectHealthNinja (Join-Path $output 'absent-ninja.exe') }
    catch { $earlyRejected = $_.Exception -is [IO.FileNotFoundException] }
    Check ($earlyRejected -and $env:StudioXRuntimeAssetsDirectory -eq 'existing-maintenance-runtime' -and !(Test-Path -LiteralPath (Join-Path $earlyOutput 'build'))) 'early input hashing failure preserves the prior runtime environment and starts no build'
}
finally { $env:StudioXRuntimeAssetsDirectory = $gateRuntimeBefore }
[IO.File]::WriteAllText((Join-Path $output 'result.json'), (@{success=$true; hardware=$false; checks=@($checks)} | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
$global:LASTEXITCODE = 0
