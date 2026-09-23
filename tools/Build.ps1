param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
& dotnet build (Join-Path $projectRoot 'StudioX.slnx') --configuration $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'StudioX build failed.' }
