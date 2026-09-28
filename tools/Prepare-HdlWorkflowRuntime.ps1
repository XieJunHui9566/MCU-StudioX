param(
    [Parameter(Mandatory = $true)][string]$SupraDirectory,
    [Parameter(Mandatory = $true)][string]$IcarusDirectory,
    [string]$RuntimeAssetsDirectory
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (!$RuntimeAssetsDirectory) { $RuntimeAssetsDirectory = Join-Path $repository 'artifacts/tool-runtime' }
$native = Join-Path $RuntimeAssetsDirectory 'toolsets/agm.logic/1.0.0'
$simulation = Join-Path $RuntimeAssetsDirectory 'toolsets/hdl.iverilog/14.0.0'
function Copy-RuntimeTree([string]$source, [string]$target) {
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        $destination = Join-Path $target ([IO.Path]::GetRelativePath($source, $file.FullName))
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
    }
}
foreach ($path in @($native, $simulation)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
# 原生 mapper 使用配套器件库；不复制 Supra 私人 license 目录。
Copy-RuntimeTree (Join-Path $SupraDirectory 'map') (Join-Path $native 'map')
Copy-Item -LiteralPath (Join-Path $repository 'licenses/Yosys-ISC.txt') -Destination $native -Force
New-Item -ItemType Directory -Path (Join-Path $simulation 'bin'), (Join-Path $simulation 'lib') -Force | Out-Null
foreach ($name in @('iverilog.exe','vvp.exe','libgcc_s_seh-1.dll','libstdc++-6.dll','libwinpthread-1.dll','libbz2-1.dll','libreadline8.dll','libhistory8.dll','libtermcap-0.dll','zlib1.dll')) {
    Copy-Item -LiteralPath (Join-Path $IcarusDirectory "bin/$name") -Destination (Join-Path $simulation 'bin') -Force
}
Copy-RuntimeTree (Join-Path $IcarusDirectory 'lib/ivl') (Join-Path $simulation 'lib/ivl')
Copy-Item -LiteralPath (Join-Path $repository 'licenses/Icarus-GPL-2.0.txt') -Destination $simulation -Force
function Write-Manifest($directory, $id, $version, $compiler, $purpose, $executables, $resources, $origin) {
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object Name -ne 'toolset.json' | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($directory, $file.FullName).Replace('\','/')
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [ordered]@{formatVersion=1;id=$id;version=$version;host='win-x64';compilerId=$compiler;purpose=$purpose;displayName=$origin;executables=$executables;resourceDirectories=$resources;sha256=$hashes} |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'toolset.json') -Encoding utf8NoBOM
}
Write-Manifest $native 'agm.logic' '1.0.0' 'agm.native' 'hdl-native' @{mapper='map/bin/yosys.exe'} @{map='map'} 'AGM Supra 2026.03 native mapper / Yosys 0.61'
Write-Manifest $simulation 'hdl.iverilog' '14.0.0' 'iverilog' 'hdl-simulation' @{iverilog='bin/iverilog.exe';vvp='bin/vvp.exe'} @{ivl='lib/ivl'} 'Icarus Verilog 14.0 devel / existing Windows distribution'
Write-Output $native
Write-Output $simulation
