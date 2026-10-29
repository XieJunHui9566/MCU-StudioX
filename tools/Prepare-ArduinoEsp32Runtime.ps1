param([Parameter(Mandatory)][string]$RuntimeDirectory, [Parameter(Mandatory)][string]$WorkingDirectory,
    [Parameter(Mandatory)][string]$AvrComponentDirectory)
$ErrorActionPreference = 'Stop'
$taskWork = [IO.Path]::GetFullPath($WorkingDirectory)
$taskTarget = [IO.Path]::GetFullPath((Join-Path $RuntimeDirectory 'toolsets/arduino.esp32/3.3.8'))
if (Test-Path -LiteralPath $taskTarget)
{
    throw 'Target component already exists; existing versions are never overwritten.'
}
$taskAvr = Get-Content -LiteralPath (Join-Path $AvrComponentDirectory 'toolset.json') -Raw | ConvertFrom-Json
$taskCli = Join-Path $AvrComponentDirectory 'arduino-cli.exe'
if ($taskAvr.componentVersions.'arduino-cli' -ne '1.5.1' -or
    (Get-FileHash -LiteralPath $taskCli).Hash -ne $taskAvr.sha256.'arduino-cli.exe')
{
    throw 'A verified Arduino CLI 1.5.1 component is required.'
}
New-Item -ItemType Directory -Path $taskWork -Force | Out-Null
$taskIndexUrl = 'https://espressif.github.io/arduino-esp32/package_esp32_index.json'
$taskConfig = Join-Path $taskWork 'cli.json'
@{ board_manager = @{ additional_urls = @($taskIndexUrl) };
    directories  = @{ data = (Join-Path $taskWork 'seed');
        downloads          = (Join-Path $taskWork 'downloads');
        user               = (Join-Path $taskWork 'user')
    }
} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $taskConfig
& $taskCli --config-file $taskConfig core update-index *> (Join-Path $taskWork 'update.log')
if ($LASTEXITCODE -ne 0)
{
    throw 'Arduino indexes failed; see update.log'
}
$taskPackage = (Get-Content -LiteralPath (Join-Path $taskWork 'seed/package_esp32_index.json') -Raw | ConvertFrom-Json).packages | Where-Object name -eq esp32
$taskPlatform = $taskPackage.platforms | Where-Object version -eq '3.3.8'
if ($taskPlatform.checksum -ne 'SHA-256:8159cefaa2eea131360ac32393d741d462583297c8c1e5169ca48139fa324173')
{
    throw 'Pinned ESP32 core index identity changed.'
}
# 官方 CLI 核对各归档的索引校验和；跳过驱动及安装脚本，始终只写独立准备目录。
& $taskCli --config-file $taskConfig core install esp32:esp32@3.3.8 --skip-post-install --skip-pre-uninstall *> (Join-Path $taskWork 'install.log')
if ($LASTEXITCODE -ne 0)
{
    throw 'Arduino ESP32 component preparation failed; see install.log'
}
$taskStage = $taskTarget + '.staging-' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $taskStage -Force | Out-Null
Copy-Item -LiteralPath $taskCli -Destination (Join-Path $taskStage 'arduino-cli.exe')
Copy-Item -LiteralPath (Join-Path $AvrComponentDirectory 'CLI-LICENSE.txt') -Destination $taskStage
Copy-Item -LiteralPath (Join-Path $taskWork 'seed') -Destination (Join-Path $taskStage 'seed') -Recurse
$taskInventory = Join-Path $taskStage 'seed/inventory.yaml'
if (Test-Path -LiteralPath $taskInventory)
{
    Remove-Item -LiteralPath $taskInventory
}
$taskAllPackages = @($taskPackage) + @((Get-Content -LiteralPath (Join-Path $taskWork 'seed/package_index.json') -Raw | ConvertFrom-Json).packages)
$taskSources = @{ cliVersion = '1.5.1';
    cliSource                = 'https://github.com/arduino/arduino-cli/releases/tag/v1.5.1';
    cliSha256                = (Get-FileHash -LiteralPath $taskCli).Hash;
    index                    = $taskIndexUrl;
    platform                 = $taskPlatform;
    dependencies             = @($taskPlatform.toolsDependencies | ForEach-Object { $taskNeed = $_; ($taskAllPackages | Where-Object name -eq $taskNeed.packager).tools | Where-Object { $_.name -eq $taskNeed.name -and $_.version -eq $taskNeed.version } })
}
$taskSources | ConvertTo-Json -Depth 14 | Set-Content -LiteralPath (Join-Path $taskStage 'sources.json')
$taskX = 'seed/packages/esp32/tools/esp-x32/2601/bin/xtensa-esp-elf-'
$taskR = 'seed/packages/esp32/tools/esp-rv32/2601/bin/riscv32-esp-elf-'
$taskHashes = [ordered]@{}
Get-ChildItem -LiteralPath $taskStage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $taskHashes[[IO.Path]::GetRelativePath($taskStage, $_.FullName).Replace('\', '/')] = (Get-FileHash -LiteralPath $_.FullName).Hash
}
@{ formatVersion        = 1;
    id                  = 'arduino.esp32';
    version             = '3.3.8';
    host                = 'win-x64';
    compilerId          = 'esp-gcc-14.2.0-2601';
    purpose             = 'arduino';
    displayName         = 'Arduino ESP32 3.3.8 / CLI 1.5.1';
    componentVersions   = @{ 'arduino-cli' = '1.5.1';
        'esp32:esp32'                      = '3.3.8'
    };
    executables         = @{ 'arduino-cli' = 'arduino-cli.exe';
        gcc                                = ($taskX + 'gcc.exe');
        gxx                                = ($taskX + 'g++.exe');
        objcopy                            = ($taskX + 'objcopy.exe');
        size                               = ($taskX + 'size.exe');
        'riscv-gcc'                        = ($taskR + 'gcc.exe');
        'riscv-gxx'                        = ($taskR + 'g++.exe')
    };
    resourceDirectories = @{ 'arduino-seed' = 'seed' };
    sha256              = $taskHashes
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $taskStage 'toolset.json')
New-Item -ItemType Directory -Path (Split-Path $taskTarget) -Force | Out-Null
# 发布前确认两端都是本轮显式指定的组件目录，禁止跨目录覆盖已有版本。
if (![IO.Path]::GetFullPath($taskStage).StartsWith($taskTarget + '.staging-', [StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $taskTarget))
{
    throw 'Component publication boundary changed.'
}
Move-Item -LiteralPath $taskStage -Destination $taskTarget
Write-Output $taskTarget
