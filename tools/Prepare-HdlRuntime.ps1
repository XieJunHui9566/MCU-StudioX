param([Parameter(Mandatory = $true)][string]$YosysExecutable, [string]$RuntimeAssetsDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$RuntimeAssetsDirectory) { $RuntimeAssetsDirectory = Join-Path $projectRoot 'artifacts/tool-runtime' }
$source = (Resolve-Path -LiteralPath $YosysExecutable).Path
$destination = Join-Path ([IO.Path]::GetFullPath($RuntimeAssetsDirectory)) 'hdl/yosys'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$version = (& $source -V | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $version -notmatch '^Yosys 0\.61 ') { throw 'This runtime recipe requires the verified Yosys 0.61 build.' }
# 仅包含独立 Yosys；不复制 Supra、厂商许可、用户工程或其它 SDK。
Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'yosys.exe') -Force
Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses/Yosys-ISC.txt') -Destination $destination -Force
$manifest = [ordered]@{
    formatVersion = 1
    executable = 'yosys.exe'
    version = $version
    sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    origin = 'Yosys 0.61 supplied with AGM Supra 2026.03.b0-a052d0a8 Windows x64'
    upstream = 'https://github.com/YosysHQ/yosys/tree/v0.61'
    license = 'Yosys-ISC.txt'
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'runtime.json') -Encoding utf8
Write-Output $destination
