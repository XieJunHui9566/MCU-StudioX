param([Parameter(Mandatory)][string]$IpeRuntimeDirectory,
    [Parameter(Mandatory)][string]$MdbJar,
    [Parameter(Mandatory)][string]$RuntimeDirectory)
$ErrorActionPreference = 'Stop'
# 私有运行组件只复用已校验的 6.20 硬件后端；不启动或安装外部 IDE。
$source = [IO.Path]::GetFullPath($IpeRuntimeDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $source 'toolset.json') -Raw | ConvertFrom-Json
if ($manifest.id -ne 'microchip.ipe' -or $manifest.version -ne '6.20.0' -or $manifest.purpose -ne 'pic-ipe')
{
    throw 'An indexed IPE 6.20 private runtime is required.'
}
foreach ($entry in $manifest.sha256.PSObject.Properties)
{
    $path = [IO.Path]::GetFullPath((Join-Path $source $entry.Name))
    if (!$path.StartsWith($source.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or
        (Get-FileHash -LiteralPath $path).Hash -ne $entry.Value)
    {
        throw ('IPE runtime content differs: ' + $entry.Name)
    }
}
# 此入口的前端来自本机官方 MPLAB X 6.35；版本与后端分别记录，不能冒称官方整套 6.20。
$frontendHash = 'B9358445C951BBF5A58C8626EAF4984121D2604C198E5777B8BD1501BE16AB3E'
if ((Get-FileHash -LiteralPath $MdbJar).Hash -ne $frontendHash)
{
    throw 'MDB command frontend does not match the validated 6.35 component.'
}
$target = Join-Path ([IO.Path]::GetFullPath($RuntimeDirectory)) 'toolsets/microchip.mdb/6.20.635'
if (Test-Path -LiteralPath $target) { throw 'Never overwrite a prepared debug component.' }
foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -Force)
{
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Debug component cannot contain links.' }
}
New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
Copy-Item -LiteralPath $source -Destination $target -Recurse
New-Item -ItemType Directory -Path (Join-Path $target 'mplab_platform/lib') -Force | Out-Null
Copy-Item -LiteralPath $MdbJar -Destination (Join-Path $target 'mplab_platform/lib/mdb.jar')
[ordered]@{backend = 'MPLAB X / IPE 6.20'; frontend = 'MDB CLI from MPLAB X 6.35';
    frontendSha256 = $frontendHash; backendManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $source 'toolset.json')).Hash;
    scope = 'StudioX private integration; compatibility must be validated separately; no external IDE or system Java';
    licenses = 'Original docs and ThirdParty retained; no redistribution grant implied'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $target 'debug-sources.json') -Encoding utf8
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $target -File -Recurse | Where-Object Name -ne 'toolset.json' | Sort-Object FullName)
{
    $hashes[[IO.Path]::GetRelativePath($target, $file.FullName).Replace('\', '/')] = (Get-FileHash -LiteralPath $file.FullName).Hash
}
[ordered]@{formatVersion = 1; id = 'microchip.mdb'; version = '6.20.635'; host = 'win-x64';
    compilerId = 'mdb-pickit3'; purpose = 'pic-mdb'; displayName = 'PICkit3 MDB / backend 6.20, CLI 6.35';
    executables = [ordered]@{java = $manifest.executables.java; mdb = 'mplab_platform/lib/mdb.jar'; ipecmd = $manifest.executables.ipecmd};
    componentVersions = [ordered]@{mdbCore = '6.20'; mdbCli = '6.35'; jre = '8.0.345'; PIC16Fxxx_DFP = '1.6.156'};
    resourceDirectories = [ordered]@{ipe = 'mplab_platform/mplab_ipe'; pic = 'packs/Microchip/PIC16Fxxx_DFP/1.6.156'};
    sha256 = $hashes} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $target 'toolset.json') -Encoding utf8
Get-FileHash -LiteralPath (Join-Path $target 'toolset.json')
