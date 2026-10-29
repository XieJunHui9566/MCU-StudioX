param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$PackSource
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Use a new output directory.'
}
$packInput = (Resolve-Path -LiteralPath $PackSource).Path
$pluginVersion = (Get-Content -LiteralPath (Join-Path $root 'plugins/StudioX.StcConfigurator/plugin.template.json') -Raw | ConvertFrom-Json).version
$packVersion = (Get-Content -LiteralPath (Join-Path $packInput 'manifest.json') -Raw | ConvertFrom-Json).version
$build = Join-Path $output 'build'
$publish = Join-Path $output 'plugin'
$emptyRuntime = Join-Path $output 'empty-runtime'
New-Item -ItemType Directory -Path $emptyRuntime -Force | Out-Null
$previousAssets = $env:StudioXRuntimeAssetsDirectory
try
{
    $env:StudioXRuntimeAssetsDirectory = $emptyRuntime
    & dotnet publish (Join-Path $root 'plugins/StudioX.StcConfigurator/StudioX.StcConfigurator.csproj') -c Release --self-contained false --artifacts-path $build -o $publish --nologo
    if ($LASTEXITCODE -ne 0)
    {
        throw 'STC configuration plugin build failed.'
    }
    Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Name -like 'StudioX.Extensions.Abstractions.*' -or $_.Extension -eq '.pdb' } | Remove-Item
    Copy-Item -LiteralPath (Join-Path $root 'plugins/StudioX.StcConfigurator/plugin.template.json') -Destination (Join-Path $publish 'plugin.json')
    & dotnet run --project (Join-Path $root 'src/StudioX.Cli/StudioX.Cli.csproj') -c Release --artifacts-path $build -- plugin pack $publish (Join-Path $output "studiox.stc15-configurator-$pluginVersion.studioxplugin")
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Configuration plugin archive failed.'
    }
    & dotnet run --project (Join-Path $root 'src/StudioX.Cli/StudioX.Cli.csproj') -c Release --artifacts-path $build -- pack $packInput (Join-Path $output "stc.iap15l2k61s2.config-preview-$packVersion.mcupack")
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Configuration preview device pack failed.'
    }
    Write-Output $output
}
finally
{
    $env:StudioXRuntimeAssetsDirectory = $previousAssets
}
