[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('list', 'plan', 'inspect', 'flash', 'configure')][string]$Command,
    [string]$Hex,
    [string]$Sha256,
    [string]$Config,
    [string]$Device,
    [string]$Log,
    [string]$Dotnet = 'dotnet',
    [switch]$NoBuild
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'StudioX.Ai8051Isp.csproj'
$binary = Join-Path $PSScriptRoot 'bin\Release\net10.0\StudioX.Ai8051Isp.dll'
if (-not $NoBuild) {
    & $Dotnet build $project -c Release --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if (-not (Test-Path -LiteralPath $binary)) { throw 'Build the tool first.' }
$arguments = @($binary, $Command)
if ($Command -in @('plan', 'flash', 'configure')) {
    if (-not $Hex) { throw 'This command requires -Hex.' }
    $firmwarePath = (Resolve-Path -LiteralPath $Hex).Path
    $arguments += @('--hex', $firmwarePath)
    if ($Command -in @('flash', 'configure')) {
        if (-not $Sha256) { $Sha256 = (Get-FileHash -LiteralPath $firmwarePath -Algorithm SHA256).Hash }
        $arguments += @('--sha256', $Sha256)
    }
}
if ($Config) { $arguments += @('--config', (Resolve-Path -LiteralPath $Config).Path) }
if ($Device) { $arguments += @('--device', $Device) }
if ($Command -in @('inspect', 'flash', 'configure')) {
    if (-not $Log) {
        $logDirectory = Join-Path (Get-Location) '.build\ai8051u-isp'
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
        $Log = Join-Path $logDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [guid]::NewGuid().ToString('N') + '.ndjson')
    }
    $arguments += @('--log', [IO.Path]::GetFullPath($Log))
}
& $Dotnet @arguments
exit $LASTEXITCODE
