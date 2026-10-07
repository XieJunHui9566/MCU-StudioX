param([string]$OutputDirectory, [string]$RuntimeAssetsDirectory, [string]$ReleaseVersion, [string]$BuildArtifactsDirectory, [string]$DevicePackCatalogDirectory, [switch]$ExcludePlugins, [ValidateSet('full', 'light', 'base')][string]$DistributionProfile = 'full', [string]$DistributionCatalogDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Distribution-Profile.ps1')
$DistributionProfile = Resolve-StudioXDistributionProfile $DistributionProfile
if ($DistributionProfile -eq 'light' -and !$OutputDirectory)
{
    throw 'Specify a new OutputDirectory for light publication.'
}
if (!$ReleaseVersion)
{
    $properties = [xml](Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw)
    $ReleaseVersion = $properties.SelectSingleNode('//ProductVersion').InnerText
}
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
. (Join-Path $PSScriptRoot 'Release-Evidence.ps1')
$releaseIdentity = Get-StudioXReleaseVersion $ReleaseVersion
if (!$RuntimeAssetsDirectory)
{
    $RuntimeAssetsDirectory = Join-Path $projectRoot 'artifacts/tool-runtime'
}
$assets = [IO.Path]::GetFullPath($RuntimeAssetsDirectory)
if ($DistributionProfile -eq 'full')
{
    $hdlRoot = Join-Path $assets 'hdl/yosys'
    foreach ($file in @('yosys.exe', 'runtime.json', 'Yosys-ISC.txt'))
    {
        if (!(Test-Path -LiteralPath (Join-Path $hdlRoot $file) -PathType Leaf))
        {
            throw "Prepare the bundled HDL preview runtime first: tools/Prepare-HdlRuntime.ps1 ($file)"
        }
    }
    $hdlManifest = Get-Content -LiteralPath (Join-Path $hdlRoot 'runtime.json') -Raw | ConvertFrom-Json
    if ($hdlManifest.sha256 -ne (Get-FileHash -LiteralPath (Join-Path $hdlRoot 'yosys.exe') -Algorithm SHA256).Hash)
    {
        throw 'Bundled Yosys executable does not match its SHA-256 manifest.'
    }
}
$buildArguments = @()
if ($BuildArtifactsDirectory)
{
    # 发布可与正在运行的开发版并存，独立 bin/obj 不触碰当前 IDE 加载的程序集。
    $BuildArtifactsDirectory = [IO.Path]::GetFullPath($BuildArtifactsDirectory)
    $buildArguments = @('--artifacts-path', $BuildArtifactsDirectory)
}
$toolsets = Join-Path $assets 'toolsets'
if ($DistributionProfile -eq 'full')
{
    if (!(Test-Path -LiteralPath $toolsets -PathType Container))
    {
        throw 'Prepare the bundled tool runtime first: tools/Prepare-ToolRuntime.ps1'
    }
    foreach ($id in @('agm.agrv', 'agm.pin-mapping', 'agm.logic', 'arm.gnu', 'riscv.xpack', 'wch.riscv', 'stc.sdcc', 'pc.mingw'))
    {
        if (!(Test-Path -LiteralPath (Join-Path $toolsets "$id/1.0.0/toolset.json")))
        {
            throw "Required bundled toolset missing: $id 1.0.0"
        }
    }
    if (!(Test-Path -LiteralPath (Join-Path $toolsets 'hdl.iverilog/14.0.0/toolset.json')))
    {
        throw 'Prepare the bundled Icarus simulation tools first: tools/Prepare-HdlWorkflowRuntime.ps1'
    }
    $mappingManifest = Get-Content -LiteralPath (Join-Path $toolsets 'agm.pin-mapping/1.0.0/toolset.json') -Raw | ConvertFrom-Json
    if ($mappingManifest.purpose -ne 'ag32-mapping' -or $mappingManifest.compilerId -ne 'agm.ve')
    {
        throw 'Prepare the bundled AGM VE/Supra tools first: tools/Prepare-Ag32MappingRuntime.ps1'
    }
    foreach ($mappingFile in Get-ChildItem -LiteralPath (Join-Path $toolsets 'agm.pin-mapping/1.0.0') -File -Recurse)
    {
        $mappingRelativePath = [IO.Path]::GetRelativePath((Join-Path $toolsets 'agm.pin-mapping/1.0.0'), $mappingFile.FullName).Replace('\', '/')
        # Python 的 LICENSE.txt 是必须保留的开源条款；Supra 的 license.txt 才是本机授权数据。
        if ($mappingRelativePath.StartsWith('supra/', [StringComparison]::OrdinalIgnoreCase) -and
            ($mappingFile.Name -eq 'license.txt' -or $mappingRelativePath -match '(^|/)license(/|$)'))
        {
            throw 'AGM user licenses must remain in private user data and cannot enter published runtime assets.'
        }
    }
    $pcManifest = Get-Content -LiteralPath (Join-Path $toolsets 'pc.mingw/1.0.0/toolset.json') -Raw | ConvertFrom-Json
    if ($pcManifest.purpose -ne 'windows-native' -or $pcManifest.compilerId -ne 'mingw-gcc-13.1.0' -or
        !(Test-Path -LiteralPath (Join-Path $toolsets ('pc.mingw/1.0.0/' + $pcManifest.executables.gcc)) -PathType Leaf) -or
        !(Test-Path -LiteralPath (Join-Path $toolsets ('pc.mingw/1.0.0/' + $pcManifest.executables.gxx)) -PathType Leaf))
    {
        throw 'Prepare the bundled Windows PC C/C++ runtime first: tools/Prepare-PcToolRuntime.ps1'
    }
    foreach ($file in @('stc-isp-portable-3.14.7/python.exe', 'stc-isp-portable-3.14.7/Scripts/stcgal.exe', 'stc-isp-portable-3.14.7/provenance.json'))
    {
        if (!(Test-Path -LiteralPath (Join-Path $assets $file) -PathType Leaf))
        {
            throw "Prepare the bundled STC ISP runtime first: $file (tools/Prepare-StcIspRuntime.ps1)"
        }
    }
}
# Espressif 的 SDK、主机工具和七个小包必须成套就绪；此检查只读取本地资源。
. (Join-Path $PSScriptRoot 'Espressif-PublishAssets.ps1')
$espressifCatalogRoot = Join-Path $projectRoot 'artifacts/packs/Espressif-0.1.1'
$espressifPacks = @(Get-EspressifReleasePacks $espressifCatalogRoot)
if ($DistributionProfile -eq 'full')
{
    Test-EspressifPublishToolsets $toolsets
}
# AGM 四个子系列从稳定的开发包索引读取，发布前完成型号、来源和路径核对。
. (Join-Path $PSScriptRoot 'Ag32-PublishAssets.ps1')
$ag32CatalogRoot = Join-Path $projectRoot 'artifacts/device-packs-development'
$ag32Packs = @(Get-Ag32ReleasePacks $ag32CatalogRoot)
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $projectRoot ('artifacts/MCUStudioX-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Publish destination must be a new directory; existing files will not be overwritten.'
}
if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/language-runtime/clangd/bin/clangd.exe')))
{
    throw 'Prepare the bundled language server first: tools/Prepare-LanguageServer.ps1'
}
# .NET 依赖还原可能访问 NuGet；厂商工具必须已准备好，此脚本不自动下载。
if (!(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/git-runtime/git/cmd/git.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/git-runtime/git/mingw64/bin/git-credential-manager.exe')) -or
    !(Test-Path -LiteralPath (Join-Path $projectRoot 'artifacts/git-runtime/git/mingw64/libexec/git-core/dlls-copied.exe')))
{
    throw 'Prepare the bundled Git first: tools/Prepare-GitRuntime.ps1'
}
& dotnet publish (Join-Path $projectRoot 'src/StudioX.Desktop/StudioX.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $output "-p:StudioXRuntimeAssetsDirectory=$assets" "-p:StudioXDistributionProfile=$DistributionProfile" "-p:Version=$($releaseIdentity.FileVersion)" "-p:ProductVersion=$ReleaseVersion" -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false --nologo @buildArguments
if ($LASTEXITCODE -ne 0)
{
    throw 'Desktop publish failed.'
}
$runtime = Join-Path $output 'runtime'
& dotnet publish (Join-Path $projectRoot 'src/StudioX.PluginHost/StudioX.PluginHost.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $runtime 'plugin-host') "-p:Version=$($releaseIdentity.FileVersion)" "-p:ProductVersion=$ReleaseVersion" -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false --nologo @buildArguments
if ($LASTEXITCODE -ne 0)
{
    throw 'Plugin host publish failed.'
}
& dotnet publish (Join-Path $projectRoot 'src/StudioX.Cli/StudioX.Cli.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $runtime 'mcp-host') "-p:Version=$($releaseIdentity.FileVersion)" "-p:ProductVersion=$ReleaseVersion" -p:DebugType=None -p:DebugSymbols=false -p:IncludeSourceRevisionInInformationalVersion=false --nologo @buildArguments
if ($LASTEXITCODE -ne 0)
{
    throw 'MCP host publish failed.'
}
if (!$ExcludePlugins)
{
    $sampleArguments = @()
    $sampleAssembly = Join-Path $projectRoot 'examples/StudioX.SampleDecoder/bin/Release/net10.0/StudioX.SampleDecoder.dll'
    if ($BuildArtifactsDirectory)
    {
        $sampleOutput = Join-Path $BuildArtifactsDirectory 'sample-plugin'
        $sampleArguments = @('--output', $sampleOutput)
        $sampleAssembly = Join-Path $sampleOutput 'StudioX.SampleDecoder.dll'
    }
    & dotnet build (Join-Path $projectRoot 'examples/StudioX.SampleDecoder/StudioX.SampleDecoder.csproj') -c Release --nologo @buildArguments @sampleArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Sample plugin build failed.'
    }
    $plugin = Join-Path $runtime 'plugins/studiox.sensor-csv'
    New-Item -ItemType Directory -Path $plugin -Force | Out-Null
    $dll = 'StudioX.SampleDecoder.dll'
    Copy-Item -LiteralPath $sampleAssembly -Destination $plugin
    $manifest = [ordered]@{
        formatVersion = 1;
        apiVersion    = 1;
        id            = 'studiox.sensor-csv';
        version       = '0.1.0';
        displayName   = 'Sensor CSV decoder'
        description   = '传感器 CSV 数据解码示例：将 temperature,0|1 解析为温度与数字状态。'
        entryAssembly = $dll;
        entryType     = 'StudioX.SampleDecoder.SensorCsvDecoder';
        capabilities  = @('decode')
        sha256        = @{ $dll = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $plugin $dll)).Hash.ToLowerInvariant() }
    }
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $plugin 'plugin.json') -Encoding utf8
    $workspacePlugin = Join-Path $runtime 'plugins/studiox.workspace-overview'
    & dotnet publish (Join-Path $projectRoot 'examples/StudioX.SamplePlugin/StudioX.SamplePlugin.csproj') -c Release --self-contained false -o $workspacePlugin --nologo @buildArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Workspace extension sample build failed.'
    }
    # 共享 SDK 类型由独立宿主提供；样例随发行附带，第一次使用仍需要显式启用。
    Get-ChildItem -LiteralPath $workspacePlugin -File | Where-Object { $_.Name -like 'StudioX.Extensions.Abstractions.*' -or $_.Extension -eq '.pdb' } | Remove-Item
    $workspaceManifest = Get-Content -LiteralPath (Join-Path $projectRoot 'examples/StudioX.SamplePlugin/plugin.template.json') -Raw | ConvertFrom-Json
    $workspaceHashes = [ordered]@{}
    foreach ($workspaceFile in Get-ChildItem -LiteralPath $workspacePlugin -File -Recurse)
    {
        $relative = [IO.Path]::GetRelativePath($workspacePlugin, $workspaceFile.FullName).Replace('\', '/')
        $workspaceHashes[$relative] = (Get-FileHash -LiteralPath $workspaceFile.FullName -Algorithm SHA256).Hash
    }
    $workspaceManifest.sha256 = $workspaceHashes
    $workspaceManifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath (Join-Path $workspacePlugin 'plugin.json') -Encoding utf8
}
elseif (Test-Path -LiteralPath (Join-Path $runtime 'plugins'))
{
    # 排除模式必须在生成前生效，不能靠删去已安装用户插件实现。
    throw 'Plugin-free publication unexpectedly contains bundled plugins.'
}
# 工具资源由 Desktop 的 Content 项统一复制，开发运行与便携发行使用同一布局。
Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $output
$publicDocs = Join-Path $output 'docs'
[IO.Directory]::CreateDirectory($publicDocs) | Out-Null
foreach ($document in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -File -Recurse -Force)
{
    # 本机硬件验收/调查记录可含板卡与工程信息，发行只附带公开功能和维护文档。
    if ($document.Name -like '*ACCEPTANCE*' -or $document.Name -like '*INVESTIGATION*' -or
        $document.Name -like '*VERIFICATION*' -or $document.Name -like 'DEVELOPMENT-CLEANUP*' -or
        $document.Name -match '-\d{8}(?:\.[^.]+)?$')
    {
        continue
    }
    $relative = [IO.Path]::GetRelativePath((Join-Path $projectRoot 'docs'), $document.FullName)
    $target = Join-Path $publicDocs $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $document.FullName -Destination $target
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'runtime/README.md') -Destination $runtime
# 只附带当前版本；STM32 按子系列分包，并按厂商放置，避免旧聚合包与新包混杂。
$preparedPacks = "$projectRoot\artifacts\packs"
if (!(Test-Path -LiteralPath $preparedPacks -PathType Container))
{
    throw "Prepared device packs directory missing: $preparedPacks"
}
$packOutput = Join-Path $output 'device-packs'
$packIndex = Get-Content -LiteralPath (Join-Path $preparedPacks 'STM32-0.1.1/index.json') -Raw | ConvertFrom-Json
$releasedPacks = @()
foreach ($entry in $espressifPacks)
{
    # 复制前再次验证来源，避免漫长发布过程中包被替换。
    $source = Resolve-EspressifPublishPath $espressifCatalogRoot $entry.file
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
    {
        throw "Espressif pack changed during publish: $source"
    }
    $relative = "Espressif/$($entry.file)"
    $target = Join-Path $packOutput $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    $releasedPacks += @{ file = $relative;
        id                    = $entry.id;
        version               = $entry.version;
        sha256                = $entry.sha256;
        devices               = $entry.devices;
        framework             = $entry.framework;
        sdkVersion            = $entry.sdkVersion;
        target                = $entry.target;
        provenanceSha256      = $entry.provenanceSha256
    }
}
foreach ($entry in $packIndex)
{
    $source = Join-Path $preparedPacks "STM32-0.1.1/$($entry.file)"
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
    {
        throw "Pack hash mismatch: $source"
    }
    $relative = "STMicroelectronics/$($entry.file)"
    $target = Join-Path $packOutput $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    $releasedPacks += @{ file =$relative;
        id                    =$entry.id;
        version               =$entry.version;
        sha256                =$entry.sha256;
        devices               =$entry.devices
    }
}
foreach ($entry in $ag32Packs)
{
    # 再次解析边界并核对文件，防止编译期间源包被替换。
    $source = Resolve-Ag32PublishPath $ag32CatalogRoot $entry.file
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
    {
        throw "AG32 pack changed during publish: $source"
    }
    $target = Join-Path $packOutput $entry.file
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $entry.sha256)
    {
        throw "AG32 copied pack hash mismatch: $target"
    }
    $releasedPacks += @{ file = $entry.file;
        id                    = $entry.id;
        version               = $entry.version;
        sha256                = $entry.sha256;
        devices               = $entry.devices;
        provenanceSha256      = $entry.provenanceSha256
    }
}
foreach ($wchPack in @('CH32V307-0.1.3-verified', 'CH32V203-0.1.1-verified', 'CH592-0.1.1-verified', 'CH595-0.1.0'))
{
    $wchIndex = Get-Content -LiteralPath (Join-Path $preparedPacks "$wchPack/index.json") -Raw | ConvertFrom-Json
    foreach ($entry in $wchIndex)
    {
        $source = Join-Path $preparedPacks "$wchPack/$($entry.file)"
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
        {
            throw "Pack hash mismatch: $source"
        }
        $relative = "WCH/$($entry.file)"
        [IO.Directory]::CreateDirectory((Join-Path $packOutput 'WCH')) | Out-Null
        Copy-Item -LiteralPath $source -Destination (Join-Path $packOutput $relative)
        $releasedPacks += @{ file =$relative;
            id                    =$entry.id;
            version               =$entry.version;
            sha256                =$entry.sha256;
            devices               =$entry.devices
        }
    }
}
foreach ($catalog in @(@{ source = 'Puya-0.1.1'; vendor = 'Puya' }, @{ source = 'Puya-Additional-0.1.0'; vendor = 'Puya' }, @{ source = 'Puya-F403-0.1.0'; vendor = 'Puya' }, @{ source = 'Puya-F410-F420-0.1.0'; vendor = 'Puya' }, @{ source = 'GD32-0.1.0-r5'; vendor = 'GigaDevice' }, @{ source = 'GD32-E51x-0.1.0'; vendor = 'GigaDevice' }, @{ source = 'STC8-0.1.0'; vendor = 'STC' }))
{
    $catalogRoot = [IO.Path]::GetFullPath((Join-Path $preparedPacks $catalog.source))
    $catalogPrefix = $catalogRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $vendorIndex = Get-Content -LiteralPath (Join-Path $catalogRoot 'index.json') -Raw | ConvertFrom-Json
    foreach ($entry in $vendorIndex)
    {
        $source = [IO.Path]::GetFullPath((Join-Path $catalogRoot $entry.file))
        if (!$source.StartsWith($catalogPrefix, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetExtension($source) -ne '.mcupack')
        {
            throw "Invalid pack path: $($entry.file)"
        }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
        {
            throw "Pack hash mismatch: $source"
        }
        $relative = ($catalog.vendor + '/' + $entry.file.Replace('\', '/'))
        $target = Join-Path $packOutput $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        $releasedPacks += @{ file =$relative;
            id                    =$entry.id;
            version               =$entry.version;
            sha256                =$entry.sha256;
            devices               =$entry.devices
        }
    }
}
foreach ($rpCatalog in @(
        @{ directory = 'RaspberryPi-MicroPython-0.2.0/RP2040-0.2.0'; id = 'raspberrypi.rp2040' },
        @{ directory = 'RaspberryPi-MicroPython-0.2.0/RP2350-0.2.0'; id = 'raspberrypi.rp2350' }
    ))
{
    $rpRoot = Join-Path $preparedPacks $rpCatalog.directory
    $rpIndex = Get-Content -LiteralPath (Join-Path $rpRoot 'index.json') -Raw | ConvertFrom-Json
    $rpSource = Join-Path $rpRoot $rpIndex.file
    if ([IO.Path]::GetFileName($rpIndex.file) -ne $rpIndex.file -or
        $rpIndex.file -ne "$($rpCatalog.id)-0.2.0.mcupack" -or
        (Get-FileHash -LiteralPath $rpSource -Algorithm SHA256).Hash -ne $rpIndex.sha256)
    {
        throw "Raspberry Pi pack path or hash mismatch: $($rpCatalog.id)"
    }
    $rpRelative = "Raspberry-Pi/$($rpIndex.file)"
    [IO.Directory]::CreateDirectory((Join-Path $packOutput 'Raspberry-Pi')) | Out-Null
    Copy-Item -LiteralPath $rpSource -Destination (Join-Path $packOutput $rpRelative)
    $releasedPacks += @{ file =$rpRelative;
        id                    =$rpCatalog.id;
        version               ='0.2.0';
        sha256                =$rpIndex.sha256;
        devices               =$rpIndex.devices
    }
}
if ($DevicePackCatalogDirectory)
{
    # 对外发行可以指定已审核的固定目录，避免把本地候选包或旧修订带入公开安装包。
    $catalogRoot = [IO.Path]::GetFullPath($DevicePackCatalogDirectory)
    $catalogIndex = Join-Path $catalogRoot 'index.json'
    $approved = @(Get-Content -LiteralPath $catalogIndex -Raw | ConvertFrom-Json)
    if (!$approved.Count -or @($approved | Group-Object id | Where-Object Count -gt 1).Count)
    {
        throw 'Invalid release pack catalog.'
    }
    $verified = @()
    foreach ($entry in $approved)
    {
        if (!$entry.file -or $entry.file -match '(^|[\\/])\.\.([\\/]|$)|:|^[\\/]' -or [IO.Path]::GetExtension($entry.file) -ne '.mcupack')
        {
            throw 'Invalid release pack path.'
        }
        $source = [IO.Path]::GetFullPath((Join-Path $catalogRoot $entry.file))
        if (!$source.StartsWith($catalogRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
        {
            throw "Release pack hash mismatch: $($entry.id)"
        }
        $verified += @{ entry = $entry;
            source            = $source
        }
    }
    foreach ($entry in $releasedPacks)
    {
        Remove-Item -LiteralPath (Join-Path $packOutput $entry.file)
    }
    foreach ($item in $verified)
    {
        $target = Join-Path $packOutput $item.entry.file
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        Copy-Item -LiteralPath $item.source -Destination $target
    }
    $releasedPacks = $approved
}
$releasedPacks | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packOutput 'index.json') -Encoding utf8
$guide = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'installer/使用说明.txt') -Raw
$guide.Replace('{{VERSION}}', $ReleaseVersion) | Set-Content -LiteralPath (Join-Path $output '使用说明.txt') -Encoding utf8
$componentInventory = @(Get-StudioXBundledComponents $output)
if ($DistributionProfile -eq 'light' -and ($componentInventory.Count -or
        (Test-Path -LiteralPath (Join-Path $runtime 'toolsets')) -or
        (Test-Path -LiteralPath (Join-Path $runtime 'hdl')) -or
        (Test-Path -LiteralPath (Join-Path $runtime 'stc-isp'))))
{
    throw 'Light publication unexpectedly contains development tools.'
}
@{formatVersion =1;
    components  =$componentInventory
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'development-components.json') -Encoding utf8
$sourceEvidence = Get-StudioXSourceEvidence $projectRoot
@{ formatVersion                 =1;
    sourceCommit                 =$sourceEvidence.sourceCommit;
    sourceDirty                  =$sourceEvidence.sourceDirty;
    product                      ='MCU StudioX';
    version                      =$ReleaseVersion;
    channel                      = $(if ($releaseIdentity.Suffix -eq 'LTS')
        {
            'lts'
        }
        else
        {
            'preview'
        });
    platform                     ='win-x64';
    updateMode                   ='installer';
    distributionProfile          =$DistributionProfile;
    bundledDevelopmentComponents = $componentInventory.Count;
    componentInventorySha256     = (Get-FileHash -LiteralPath (Join-Path $output 'development-components.json')).Hash.ToLowerInvariant();
    userDataDirectory            ='%LOCALAPPDATA%\MCUStudioX';
    devicePacksDirectory         ='device-packs';
    bundledPlugins               = !$ExcludePlugins.IsPresent
    devicePackCatalogSha256      = (Get-FileHash -LiteralPath (Join-Path $packOutput 'index.json') -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
if ($DistributionCatalogDirectory)
{
    & (Join-Path $PSScriptRoot 'Copy-DistributionCatalog.ps1') -SourceDirectory $DistributionCatalogDirectory -OutputDirectory (Join-Path $output 'runtime/distribution')
}
Write-Output $output
