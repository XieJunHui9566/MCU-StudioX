param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = '22.1.6'
$expectedHash = 'CE54F16E0B4FD76D450EEDA9664420B195360B73FEBCFE40E661108FA57F2CE1'
$destination = Join-Path $projectRoot 'artifacts/language-runtime/clangd'
if (Test-Path -LiteralPath (Join-Path $destination 'bin/clangd.exe')) { Write-Output $destination; return }
$downloadDirectory = Join-Path $projectRoot '.artifacts/downloads'
New-Item -ItemType Directory -Path $downloadDirectory -Force | Out-Null
$archive = Join-Path $downloadDirectory "clangd-windows-$version.zip"
if (!(Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/clangd/clangd/releases/download/$version/clangd-windows-$version.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'clangd archive SHA-256 mismatch.' }
$staging = Join-Path $projectRoot ('.artifacts/clangd-extract-' + [Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archive -DestinationPath $staging
$source = Join-Path $staging "clangd_$version"
if (!(Test-Path -LiteralPath (Join-Path $source 'bin/clangd.exe'))) { throw 'clangd executable missing from archive.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
# 固定来源及摘要，只复制到仓库的构建资源目录，不修改 PATH 或系统安装。
Copy-Item -LiteralPath $source -Destination $destination -Recurse
Write-Output $destination
