param([Parameter(Mandatory)][string]$OutputDirectory, [string]$ReleaseVersion, [string]$BuildArtifactsDirectory, [string]$DistributionCatalogDirectory)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
if (!$ReleaseVersion) {
    $taskProperties = [xml](Get-Content -LiteralPath (Join-Path $taskRoot 'Directory.Build.props') -Raw)
    $ReleaseVersion = $taskProperties.SelectSingleNode('//ProductVersion').InnerText
}
$taskIdentity = Get-StudioXReleaseVersion $ReleaseVersion
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'Base publication requires a new output directory.' }
foreach ($taskFile in @('artifacts/language-runtime/clangd/bin/clangd.exe', 'artifacts/git-runtime/git/cmd/git.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $taskRoot $taskFile))) { throw "Required core runtime missing: $taskFile" }
}
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
$taskEmpty = Join-Path $taskOutput '.empty-runtime-assets'
[IO.Directory]::CreateDirectory($taskEmpty) | Out-Null
$taskArguments = @()
if ($BuildArtifactsDirectory) { $taskArguments = @('--artifacts-path', [IO.Path]::GetFullPath($BuildArtifactsDirectory)) }
# 基础发行仍自包含 .NET、语言服务、Git 和独立插件宿主；工具链按工程明确版本另行安装。
foreach ($taskProject in @(
    @{ name='StudioX.Desktop'; output=$taskOutput },
    @{ name='StudioX.PluginHost'; output=(Join-Path $taskOutput 'runtime/plugin-host') },
    @{ name='StudioX.Cli'; output=(Join-Path $taskOutput 'runtime/mcp-host') }
)) {
    & dotnet publish (Join-Path $taskRoot "src/$($taskProject.name)/$($taskProject.name).csproj") -c Release -r win-x64 --self-contained true -o $taskProject.output "-p:StudioXRuntimeAssetsDirectory=$taskEmpty" "-p:Version=$($taskIdentity.FileVersion)" "-p:ProductVersion=$ReleaseVersion" -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false --nologo @taskArguments
    if ($LASTEXITCODE -ne 0) { throw "Base publication failed: $($taskProject.name)" }
}
if (Test-Path -LiteralPath (Join-Path $taskOutput 'runtime/toolsets')) { throw 'Base publication unexpectedly contains firmware toolsets.' }
[IO.Directory]::CreateDirectory((Join-Path $taskOutput 'device-packs')) | Out-Null
[IO.File]::WriteAllText((Join-Path $taskOutput 'device-packs/index.json'), '[]')
$taskGuide = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/使用说明.txt') -Raw
($taskGuide.Replace('{{VERSION}}', $ReleaseVersion) + "`n基础版：从工具 → 软件与组件分发安装工程所需的工具集；也支持 .studioxtools 离线归档。`n") | Set-Content -LiteralPath (Join-Path $taskOutput '使用说明.txt') -Encoding utf8
@{formatVersion=1;product='MCU StudioX';version=$ReleaseVersion;channel='preview';platform='win-x64';distributionProfile='base';updateMode='installer';userDataDirectory='%LOCALAPPDATA%\MCUStudioX';devicePacksDirectory='device-packs';bundledPlugins=$false;devicePackCatalogSha256=(Get-FileHash -LiteralPath (Join-Path $taskOutput 'device-packs/index.json') -Algorithm SHA256).Hash} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskOutput 'release.json') -Encoding utf8
if ($DistributionCatalogDirectory) { & (Join-Path $PSScriptRoot 'Copy-DistributionCatalog.ps1') -SourceDirectory $DistributionCatalogDirectory -OutputDirectory (Join-Path $taskOutput 'runtime/distribution') }
Write-Output $taskOutput
