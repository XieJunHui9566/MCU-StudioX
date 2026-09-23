param([Parameter(Mandatory=$true)][string]$ToolchainDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$source = [IO.Path]::GetFullPath((Join-Path $ToolchainDirectory 'riscv64-unknown-elf/include'))
if ((Get-Content -LiteralPath (Join-Path $source '_newlib_version.h') -Raw) -notmatch '"4\.1\.0"') { throw 'This recipe requires the AgRV GCC 11.1.0 newlib 4.1.0 headers.' }
$destination = Join-Path $projectRoot 'artifacts/language-runtime/sysroots/agrv-gcc-11.1.0'
New-Item -ItemType Directory -Path (Join-Path $destination 'include') -Force | Out-Null
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $source -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($source, $file.FullName).Replace('\','/')
    if ($relative.StartsWith('c++/')) { continue }
    $target = Join-Path $destination ('include/' + $relative)
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $hashes[$relative] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses/Newlib-COPYING.txt') -Destination $destination
[ordered]@{ compilerId='agrv-gcc-11.1.0'; library='newlib'; version='4.1.0'; purpose='C headers for language analysis only'; sha256=$hashes } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'manifest.json') -Encoding utf8
Write-Output $destination
