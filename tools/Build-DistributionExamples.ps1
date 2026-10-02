param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'Distribution examples require a new output directory.' }
[IO.Directory]::CreateDirectory($taskOutput) | Out-Null
& (Join-Path $PSScriptRoot 'Build-PluginSample.ps1') -Development -OutputDirectory (Join-Path $taskOutput 'plugin')
& (Join-Path $PSScriptRoot 'Build-ComponentSample.ps1') -OutputFile (Join-Path $taskOutput 'studiox.byte-utils-1.0.0.studioxcomponent')
# 整体项目尚未声明开源许可证，插件示例如实记录 NOASSERTION，不能替作者虚构许可。
@{publisher='MCU StudioX 开发示例';entries=@(
    @{kind='plugin';archive='plugin/studiox.development-1.0.0.studioxplugin';license='NOASSERTION';sourceUrl='https://github.com/XieJunHui9566/MCU-StudioX';releaseNotes='开发示例：语言、设置、只读调试快照面板；安装后需显式启用。'},
    @{kind='component';archive='studiox.byte-utils-1.0.0.studioxcomponent';license='MIT';sourceUrl='https://github.com/XieJunHui9566/MCU-StudioX';releaseNotes='字节读取头文件示例；支持 CMake 和 ESP-IDF。'}
)} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskOutput 'metadata.json') -Encoding utf8
& (Join-Path $PSScriptRoot 'New-DistributionCatalog.ps1') -MetadataFile (Join-Path $taskOutput 'metadata.json') -OutputFile (Join-Path $taskOutput 'catalog.json')
