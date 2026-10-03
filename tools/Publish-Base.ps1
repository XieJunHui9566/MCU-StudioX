param([Parameter(Mandatory)][string]$OutputDirectory, [string]$RuntimeAssetsDirectory, [string]$ReleaseVersion,
    [string]$BuildArtifactsDirectory, [string]$DevicePackCatalogDirectory, [switch]$ExcludePlugins, [string]$DistributionCatalogDirectory)
$ErrorActionPreference = 'Stop'
# 保留既有脚本入口，base 现在明确指向轻量版，不再裁掉器件包和内置插件。
& (Join-Path $PSScriptRoot 'Publish-Light.ps1') @PSBoundParameters
