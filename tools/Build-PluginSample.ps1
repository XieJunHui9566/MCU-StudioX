param([string]$OutputDirectory, [string]$BuildArtifactsDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $projectRoot 'artifacts/plugin-sdk'
}
$pluginOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $pluginOutput)
{
    throw 'Use a new output directory; existing developer artifacts are not overwritten.'
}
if (!$BuildArtifactsDirectory)
{
    $BuildArtifactsDirectory = Join-Path $pluginOutput 'build'
}
$buildArguments = @('--artifacts-path', [IO.Path]::GetFullPath($BuildArtifactsDirectory))
$publishDirectory = Join-Path $pluginOutput 'studiox.workspace-overview'
& dotnet publish (Join-Path $projectRoot 'examples/StudioX.SamplePlugin/StudioX.SamplePlugin.csproj') -c Release --self-contained false -o $publishDirectory @buildArguments
if ($LASTEXITCODE -ne 0)
{
    throw 'Plugin sample build failed.'
}
# 共享契约由宿主提供；归档只包含插件自身的发布文件，避免私有契约类型副本。
Get-ChildItem -LiteralPath $publishDirectory -File | Where-Object { $_.Name -like 'StudioX.Extensions.Abstractions.*' -or $_.Extension -eq '.pdb' } | Remove-Item
Copy-Item -LiteralPath (Join-Path $projectRoot 'examples/StudioX.SamplePlugin/plugin.template.json') -Destination (Join-Path $publishDirectory 'plugin.json')
& dotnet pack (Join-Path $projectRoot 'src/StudioX.Extensions.Abstractions/StudioX.Extensions.Abstractions.csproj') -c Release -o (Join-Path $pluginOutput 'nuget') @buildArguments
if ($LASTEXITCODE -ne 0)
{
    throw 'Plugin SDK package build failed.'
}
& dotnet run --project (Join-Path $projectRoot 'src/StudioX.Cli/StudioX.Cli.csproj') -c Release @buildArguments -- plugin pack $publishDirectory (Join-Path $pluginOutput 'studiox.workspace-overview-1.0.0.studioxplugin')
if ($LASTEXITCODE -ne 0)
{
    throw 'Plugin archive validation failed.'
}
$manifest = Get-Content -LiteralPath (Join-Path $publishDirectory 'plugin.json') -Raw | ConvertFrom-Json
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)
{
    if ($file.Name -ne 'plugin.json')
    {
        $relative = [IO.Path]::GetRelativePath($publishDirectory, $file.FullName).Replace('\', '/')
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}
$manifest.sha256 = $hashes
$manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $publishDirectory 'plugin.json') -Encoding utf8
Write-Output $pluginOutput
