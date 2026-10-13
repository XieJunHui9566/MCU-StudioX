param([Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$BuildArtifactsDirectory,
    [string]$LanguageRuntime)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = [IO.Path]::GetFullPath($BuildArtifactsDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new synchronization output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'StudioX.ProjectSynchronizationValidation/StudioX.ProjectSynchronizationValidation.csproj') -c Release --artifacts-path $build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Synchronization validator build failed.' }
$validator = Join-Path $build 'bin/StudioX.ProjectSynchronizationValidation/release_win-x64/StudioX.ProjectSynchronizationValidation.exe'
$validationArgs = @((Join-Path $output 'application'))
if ($LanguageRuntime) { $validationArgs += [IO.Path]::GetFullPath($LanguageRuntime) }
& $validator @validationArgs
if ($LASTEXITCODE -ne 0) { throw 'Synchronization service checks failed.' }
$desktop = Join-Path $build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe'
$ui = Join-Path $output 'ui'
$process = Start-Process -FilePath $desktop -ArgumentList @('--preview-project-synchronization', ('"' + $ui + '"')) -WindowStyle Hidden -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(60)
while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath (Join-Path $ui 'error.txt')) {
        $process.Kill($true)
        throw (Get-Content -LiteralPath (Join-Path $ui 'error.txt') -Raw)
    }
    Start-Sleep -Milliseconds 100
    $process.Refresh()
}
if (!$process.HasExited) {
    $process.Kill($true)
    throw 'Synchronization UI timed out.'
}
if ($process.ExitCode -ne 0) { throw 'Synchronization UI failed.' }
$uiResult = Get-Content -LiteralPath (Join-Path $ui 'result.json') -Raw | ConvertFrom-Json
$applicationResult = Get-Content -LiteralPath (Join-Path $output 'application/result.json') -Raw | ConvertFrom-Json
if (!$uiResult.success -or !$applicationResult.success) { throw 'Synchronization did not report success.' }
[IO.File]::WriteAllText((Join-Path $output 'result.json'), (@{
    success = $true; applicationChecks = $applicationResult.checks.Count; uiChecks = $uiResult.checks.Count
    language = $applicationResult.language; hardware = $false
} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$global:LASTEXITCODE = 0
