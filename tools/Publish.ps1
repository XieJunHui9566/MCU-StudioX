param([string]$OutputDirectory, [string]$RuntimeAssetsDirectory, [string]$ReleaseVersion)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$ReleaseVersion) { $ReleaseVersion = ([xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version }
if ($ReleaseVersion -notmatch '^\d+\.\d+\.\d+$' -or ($ReleaseVersion.Split('.') | Where-Object { [int]$_ -gt 65535 }).Count) { throw 'ReleaseVersion must be a three-part numeric version.' }
if (!$RuntimeAssetsDirectory) { $RuntimeAssetsDirectory = Join-Path $projectRoot 'artifacts/tool-runtime' }
$assets = [IO.Path]::GetFullPath($RuntimeAssetsDirectory)
$toolsets = Join-Path $assets 'toolsets'
if (!(Test-Path -LiteralPath $toolsets -PathType Container)) { throw 'Prepare the bundled tool runtime first: tools/Prepare-ToolRuntime.ps1' }
foreach ($id in @('agm.agrv','arm.gnu','riscv.xpack','wch.riscv','stc.sdcc')) {
    if (!(Test-Path -LiteralPath (Join-Path $toolsets "$id/1.0.0/toolset.json"))) { throw "Required bundled toolset missing: $id 1.0.0" }
}
foreach ($file in @('stc-isp-portable-3.14.7/python.exe', 'stc-isp-portable-3.14.7/Scripts/stcgal.exe', 'stc-isp-portable-3.14.7/provenance.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $assets $file) -PathType Leaf)) { throw "Prepare the bundled STC ISP runtime first: $file (tools/Prepare-StcIspRuntime.ps1)" }
}
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot ('artifacts/MCUStudioX-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Publish destination must be a new directory; existing files will not be overwritten.' }
if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/language-runtime/clangd/bin/clangd.exe'))) {
    throw 'Prepare the bundled language server first: tools/Prepare-LanguageServer.ps1'
}
# SDK restore/publish may use NuGet. Vendor tools are never downloaded by this script.
if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/git-runtime/git/cmd/git.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/git-runtime/git/mingw64/bin/git-credential-manager.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/git-runtime/git/mingw64/libexec/git-core/dlls-copied.exe'))) {
    throw 'Prepare the bundled Git first: tools/Prepare-GitRuntime.ps1'
}
& dotnet publish (Join-Path $projectRoot 'src/StudioX.Desktop/StudioX.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $output "-p:StudioXRuntimeAssetsDirectory=$assets" "-p:Version=$ReleaseVersion" -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
$runtime = Join-Path $output 'runtime'
& dotnet publish (Join-Path $projectRoot 'src/StudioX.PluginHost/StudioX.PluginHost.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $runtime 'plugin-host') "-p:Version=$ReleaseVersion" -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'Plugin host publish failed.' }
& dotnet publish (Join-Path $projectRoot 'src/StudioX.Cli/StudioX.Cli.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $runtime 'mcp-host') "-p:Version=$ReleaseVersion" -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw 'MCP host publish failed.' }
& dotnet build (Join-Path $projectRoot 'examples/StudioX.SampleDecoder/StudioX.SampleDecoder.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Sample plugin build failed.' }
$plugin = Join-Path $runtime 'plugins/studiox.sensor-csv'
New-Item -ItemType Directory -Path $plugin -Force | Out-Null
$dll = 'StudioX.SampleDecoder.dll'
Copy-Item -LiteralPath (Join-Path $projectRoot "examples/StudioX.SampleDecoder/bin/Release/net10.0/$dll") -Destination $plugin
$manifest = [ordered]@{
    formatVersion = 1; apiVersion = 1; id = 'studiox.sensor-csv'; version = '0.1.0'; displayName = 'Sensor CSV decoder'
    entryAssembly = $dll; entryType = 'StudioX.SampleDecoder.SensorCsvDecoder'; capabilities = @('decode')
    sha256 = @{ $dll = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $plugin $dll)).Hash.ToLowerInvariant() }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $plugin 'plugin.json') -Encoding utf8
# 工具资源由 Desktop 的 Content 项统一复制，开发运行与便携发行使用同一布局。
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs') -Destination $output -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'runtime/README.md') -Destination $runtime
# 只附带当前版本；STM32 按子系列分包，并按厂商放置，避免旧聚合包与新包混杂。
$preparedPacks = "$projectRoot\artifacts\packs"
if (!(Test-Path -LiteralPath $preparedPacks -PathType Container)) { throw "Prepared device packs directory missing: $preparedPacks" }
$packOutput = Join-Path $output 'device-packs'
$packIndex = Get-Content -LiteralPath (Join-Path $preparedPacks 'STM32-0.1.1/index.json') -Raw | ConvertFrom-Json
$releasedPacks = @()
foreach ($entry in $packIndex) {
    $source = Join-Path $preparedPacks "STM32-0.1.1/$($entry.file)"
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Pack hash mismatch: $source" }
    $relative = "STMicroelectronics/$($entry.file)"
    $target = Join-Path $packOutput $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    $releasedPacks += @{ file=$relative; id=$entry.id; version=$entry.version; sha256=$entry.sha256; devices=$entry.devices }
}
$agSource = Join-Path $preparedPacks 'studiox.preview.ag32vf303-0.1.1.mcupack'
[IO.Directory]::CreateDirectory((Join-Path $packOutput 'AGM')) | Out-Null
Copy-Item -LiteralPath $agSource -Destination (Join-Path $packOutput 'AGM')
$releasedPacks += @{ file=('AGM/' + [IO.Path]::GetFileName($agSource)); id='studiox.preview.ag32vf303'; version='0.1.1'; sha256=(Get-FileHash -LiteralPath $agSource -Algorithm SHA256).Hash.ToLowerInvariant(); devices=@('AG32VF303CCT6') }
foreach ($wchPack in @('CH32V307-0.1.1','CH32V203-0.1.0-verified','CH592-0.1.0','CH595-0.1.0')) {
  $wchIndex = Get-Content -LiteralPath (Join-Path $preparedPacks "$wchPack/index.json") -Raw | ConvertFrom-Json
  foreach ($entry in $wchIndex) {
    $source = Join-Path $preparedPacks "$wchPack/$($entry.file)"
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Pack hash mismatch: $source" }
    $relative = "WCH/$($entry.file)"
    [IO.Directory]::CreateDirectory((Join-Path $packOutput 'WCH')) | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $packOutput $relative)
    $releasedPacks += @{ file=$relative; id=$entry.id; version=$entry.version; sha256=$entry.sha256; devices=$entry.devices }
  }
}
foreach ($catalog in @(@{ source='Puya-0.1.1'; vendor='Puya' }, @{ source='Puya-Additional-0.1.0'; vendor='Puya' }, @{ source='Puya-F403-0.1.0'; vendor='Puya' }, @{ source='Puya-F410-F420-0.1.0'; vendor='Puya' }, @{ source='GD32-0.1.0-r5'; vendor='GigaDevice' }, @{ source='GD32-E51x-0.1.0'; vendor='GigaDevice' }, @{ source='STC8-0.1.0'; vendor='STC' })) {
  $catalogRoot = [IO.Path]::GetFullPath((Join-Path $preparedPacks $catalog.source))
  $catalogPrefix = $catalogRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
  $vendorIndex = Get-Content -LiteralPath (Join-Path $catalogRoot 'index.json') -Raw | ConvertFrom-Json
  foreach ($entry in $vendorIndex) {
    $source = [IO.Path]::GetFullPath((Join-Path $catalogRoot $entry.file))
    if (!$source.StartsWith($catalogPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetExtension($source) -ne '.mcupack') { throw "Invalid pack path: $($entry.file)" }
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Pack hash mismatch: $source" }
    $relative = ($catalog.vendor + '/' + $entry.file.Replace('\', '/'))
    $target = Join-Path $packOutput $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    $releasedPacks += @{ file=$relative; id=$entry.id; version=$entry.version; sha256=$entry.sha256; devices=$entry.devices }
  }
}
$rpIndex = Get-Content -LiteralPath (Join-Path $preparedPacks 'RP2350-0.1.0-verified/index.json') -Raw | ConvertFrom-Json
$rpSource = Join-Path $preparedPacks "RP2350-0.1.0-verified/$($rpIndex.file)"
if ([IO.Path]::GetFileName($rpIndex.file) -ne $rpIndex.file -or
    [IO.Path]::GetExtension($rpIndex.file) -ne '.mcupack' -or
    (Get-FileHash -LiteralPath $rpSource -Algorithm SHA256).Hash -ne $rpIndex.sha256) {
    throw 'RP2350 pack path or hash mismatch.'
}
$rpRelative = "Raspberry-Pi/$($rpIndex.file)"
[IO.Directory]::CreateDirectory((Join-Path $packOutput 'Raspberry-Pi')) | Out-Null
Copy-Item -LiteralPath $rpSource -Destination (Join-Path $packOutput $rpRelative)
$releasedPacks += @{ file=$rpRelative; id='raspberrypi.rp2350'; version='0.1.0'; sha256=$rpIndex.sha256; devices=$rpIndex.devices }
$releasedPacks | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packOutput 'index.json') -Encoding utf8
$guide = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/使用说明.txt') -Raw
$guide.Replace('{{VERSION}}', $ReleaseVersion) | Set-Content -LiteralPath (Join-Path $output '使用说明.txt') -Encoding utf8
@{ formatVersion=1; product='MCU StudioX'; version=$ReleaseVersion; channel='preview'; platform='win-x64'; updateMode='installer'; userDataDirectory='%LOCALAPPDATA%\MCUStudioX'; devicePacksDirectory='device-packs' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
Write-Output $output
