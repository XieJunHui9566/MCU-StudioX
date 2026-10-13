param([Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$BuildArtifactsDirectory,
    [string]$ValidationInputs)
$ErrorActionPreference = 'Stop'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$build = [IO.Path]::GetFullPath($BuildArtifactsDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new source registration output directory.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
& dotnet build (Join-Path $PSScriptRoot 'StudioX.SourceRegistrationValidation/StudioX.SourceRegistrationValidation.csproj') -c Release --artifacts-path $build --nologo
if ($LASTEXITCODE -ne 0) { throw 'Source registration validator build failed.' }
$validationArgs = @((Join-Path $output 'application'))
if ($ValidationInputs) { $validationArgs += [IO.Path]::GetFullPath($ValidationInputs) }
& (Join-Path $build 'bin/StudioX.SourceRegistrationValidation/release_win-x64/StudioX.SourceRegistrationValidation.exe') @validationArgs
if ($LASTEXITCODE -ne 0) { throw 'Source registration application checks failed.' }
$ui = Join-Path $output 'ui'
$process = Start-Process -FilePath (Join-Path $build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe') -ArgumentList @('--preview-source-registration', ('"' + $ui + '"')) -WindowStyle Hidden -PassThru
$deadline = [DateTime]::UtcNow.AddSeconds(90)
while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    if (Test-Path -LiteralPath (Join-Path $ui 'error.txt')) { $process.Kill($true); throw (Get-Content -LiteralPath (Join-Path $ui 'error.txt') -Raw) }
    Start-Sleep -Milliseconds 100
    $process.Refresh()
}
if (!$process.HasExited) { $process.Kill($true); throw 'Source registration UI timed out.' }
if ($process.ExitCode -ne 0) { throw 'Source registration UI failed.' }
$application = Get-Content -LiteralPath (Join-Path $output 'application/result.json') -Raw | ConvertFrom-Json
$uiResult = Get-Content -LiteralPath (Join-Path $ui 'result.json') -Raw | ConvertFrom-Json
if (!$application.success -or !$uiResult.success) { throw 'Source registration did not report success.' }
[IO.File]::WriteAllText((Join-Path $output 'result.json'), (@{
    success = $true; applicationChecks = $application.checks.Count; uiChecks = $uiResult.checks.Count
    realCompilation = $application.realCompilation; hardware = $false
} | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
$global:LASTEXITCODE = 0
