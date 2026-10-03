param([Parameter(Mandatory)][string]$OutputDirectory, [string]$RuntimeAssetsDirectory, [string]$ReleaseVersion,
    [string]$BuildArtifactsDirectory, [string]$DevicePackCatalogDirectory, [switch]$ExcludePlugins, [string]$DistributionCatalogDirectory)
$ErrorActionPreference = 'Stop'
# 共用完整发行的器件包、插件和文档流程；只在 MSBuild 内容选择时排除开发工具。
& (Join-Path $PSScriptRoot 'Publish.ps1') @PSBoundParameters -DistributionProfile light
