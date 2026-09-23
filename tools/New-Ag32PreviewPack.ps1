param(
    [Parameter(Mandatory = $true)][string]$SdkDirectory,
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$sdk = [IO.Path]::GetFullPath($SdkDirectory)
$recipe = Join-Path $projectRoot 'examples/packs/agm.ag32vf303-preview'
$cli = Join-Path $projectRoot 'src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll'
if (!(Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'Run tools/Build.ps1 first.' }
foreach ($relative in @('package.json', 'misc/crt.S', 'misc/syscalls.c', 'misc/agrv.h', 'misc/encoding.h', 'misc/init.ld', 'misc/section.ld', 'misc/devices/AgRV2K_mem.ld', 'misc/devices/AgRV2K_FLASH.ld', 'src/interrupt.c', 'src/AltaRiscv.svd')) {
    if (!(Test-Path -LiteralPath (Join-Path $sdk $relative) -PathType Leaf)) { throw "SDK input missing: $relative" }
}
$packVersion = (Get-Content -LiteralPath (Join-Path $recipe 'manifest.json') -Raw | ConvertFrom-Json).version
if (!$OutputFile) { $OutputFile = Join-Path $projectRoot "artifacts/packs/studiox.preview.ag32vf303-$packVersion.mcupack" }
$output = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $output) { throw 'Output already exists. Choose a new filename; existing packages will not be overwritten.' }
$staging = Join-Path $projectRoot ('.artifacts/ag32-preview-source-' + [Guid]::NewGuid().ToString('N'))
foreach ($relative in @('sdk/include', 'sdk/startup', 'sdk/src', 'svd', 'vendor', 'linker')) {
    New-Item -ItemType Directory -Path (Join-Path $staging $relative) -Force | Out-Null
}
Get-ChildItem -LiteralPath $recipe | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse }
Get-ChildItem -LiteralPath (Join-Path $sdk 'src') -Filter '*.h' -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $staging 'sdk/include')
}
foreach ($name in @('crt.S', 'syscalls.c', 'agrv.h', 'encoding.h')) {
    Copy-Item -LiteralPath (Join-Path $sdk "misc/$name") -Destination (Join-Path $staging 'sdk/startup')
}
Copy-Item -LiteralPath (Join-Path $sdk 'src/interrupt.c') -Destination (Join-Path $staging 'sdk/src')
Copy-Item -LiteralPath (Join-Path $sdk 'src/AltaRiscv.svd') -Destination (Join-Path $staging 'svd')
Copy-Item -LiteralPath (Join-Path $sdk 'package.json') -Destination (Join-Path $staging 'vendor/framework-agrv_sdk.json')

# Flatten vendor linker INCLUDEs so projects do not depend on SDK installation paths.
$memory = Get-Content -LiteralPath (Join-Path $sdk 'misc/devices/AgRV2K_mem.ld') -Raw
if ($memory -notmatch 'PROVIDE\(FLASH_SIZE\s*=\s*16M\);') { throw 'SDK Flash declaration changed; review linker layout before creating this pack.' }
$memory = $memory -replace 'PROVIDE\(FLASH_SIZE\s*=\s*16M\);', 'PROVIDE(FLASH_SIZE = 0x27000);'
$flash = Get-Content -LiteralPath (Join-Path $sdk 'misc/devices/AgRV2K_FLASH.ld') -Raw
$flash = $flash -replace '(?m)^INCLUDE\s+[^\r\n]+\r?\n?', ''
$init = Get-Content -LiteralPath (Join-Path $sdk 'misc/init.ld') -Raw
$sections = Get-Content -LiteralPath (Join-Path $sdk 'misc/section.ld') -Raw
$linker = "/* AG32VF303: first 156 KiB for firmware, final 100 KiB reserved for logic. */`n" + $memory + "`n" + $flash + "`n" + $init + "`n" + $sections
[IO.File]::WriteAllText((Join-Path $staging 'linker/ag32vf303.ld'), $linker, [Text.UTF8Encoding]::new($false))
& dotnet $cli pack $staging $output
if ($LASTEXITCODE -ne 0) { throw 'StudioX pack creation failed.' }
Get-Item -LiteralPath $output | Select-Object FullName,Length
