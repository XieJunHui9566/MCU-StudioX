param(
    [string]$RuntimeDirectory,
    [string]$WorkingDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $RuntimeDirectory)
{
    $RuntimeDirectory = Join-Path $projectRoot 'runtime'
}
if (-not $WorkingDirectory)
{
    $WorkingDirectory = Join-Path $projectRoot ('.artifacts/arduino-runtime-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
$taskWork = [IO.Path]::GetFullPath($WorkingDirectory)
$target = [IO.Path]::GetFullPath((Join-Path $RuntimeDirectory 'toolsets/arduino.avr/1.8.8'))
if (Test-Path -LiteralPath $target)
{
    throw 'Target component already exists; existing versions are never overwritten.'
}
New-Item -ItemType Directory -Path $taskWork -Force | Out-Null
$zip = Join-Path $taskWork 'cli.zip'
$cliUrl = 'https://github.com/arduino/arduino-cli/releases/download/v1.5.1/arduino-cli_1.5.1_Windows_64bit.zip'
if (-not (Test-Path -LiteralPath $zip))
{
    Invoke-WebRequest $cliUrl -OutFile $zip
}
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne 'FABE42E0EB04D00E776A66178299FF95A46C623DBC260F997E58FD514853DD40')
{
    throw 'Arduino CLI SHA-256 mismatch.'
}
if (-not (Test-Path -LiteralPath (Join-Path $taskWork 'cli/arduino-cli.exe')))
{
    Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $taskWork 'cli')
}
$cli = Join-Path $taskWork 'cli/arduino-cli.exe'
$config = Join-Path $taskWork 'arduino-cli.yaml'
$settings = @{ directories = @{ data = (Join-Path $taskWork 'seed');
        downloads                    = (Join-Path $taskWork 'downloads');
        user                         = (Join-Path $taskWork 'user')
    }
}
[IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 5))
# 仅准备固定版本的官方工具；跳过驱动安装脚本，不更改 PATH，也不打开板卡端口。
if (-not (Test-Path -LiteralPath (Join-Path $taskWork 'seed/packages/arduino/hardware/avr/1.8.8/platform.txt')))
{
    & $cli --config-file $config core update-index *> (Join-Path $taskWork 'update.log')
    if ($LASTEXITCODE -ne 0)
    {
        throw "Arduino index failed; see $taskWork/update.log"
    }
    & $cli --config-file $config core install arduino:avr@1.8.8 --skip-post-install --skip-pre-uninstall *> (Join-Path $taskWork 'install.log')
    if ($LASTEXITCODE -ne 0)
    {
        throw "Arduino core failed; see $taskWork/install.log"
    }
}
$stage = $target + '.staging-' + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Path $stage -Force | Out-Null
Copy-Item -LiteralPath $cli -Destination (Join-Path $stage 'arduino-cli.exe')
Copy-Item -LiteralPath (Join-Path $taskWork 'cli/LICENSE.txt') -Destination (Join-Path $stage 'CLI-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $taskWork 'seed') -Destination (Join-Path $stage 'seed') -Recurse
# CLI 首次运行的安装标识属于本机用户数据，不随工具组件分发；使用者会在私有目录生成自己的标识。
$inventory = Join-Path $stage 'seed/inventory.yaml'
if (Test-Path -LiteralPath $inventory)
{
    Remove-Item -LiteralPath $inventory
}
$sourceIndex = Get-Content -LiteralPath (Join-Path $taskWork 'seed/package_index.json') -Raw | ConvertFrom-Json
$platform = ($sourceIndex.packages | Where-Object name -eq arduino).platforms | Where-Object { $_.architecture -eq 'avr' -and $_.version -eq '1.8.8' }
if ($platform.checksum -ne 'SHA-256:a234c2a43dcd01ce54be665806f183b8e6ec4e966d2e3e0c3358b63023d6390c')
{
    throw 'AVR core index identity changed.'
}
$dependencySources = foreach ($dependency in $platform.toolsDependencies)
{
    $package = $sourceIndex.packages | Where-Object name -eq $dependency.packager
    $tool = $package.tools | Where-Object { $_.name -eq $dependency.name -and $_.version -eq $dependency.version }
    @{ packager         = $dependency.packager;
        name            = $dependency.name;
        version         = $dependency.version;
        windowsArchives = @($tool.systems | Where-Object host -Match 'mingw32')
    }
}
$sources = @{ cli = @{ version = '1.5.1';
        url                    = $cliUrl;
        sha256                 = (Get-FileHash $zip).Hash
    };
    platform      = $platform;
    dependencies  = @($dependencySources);
    index         = 'https://downloads.arduino.cc/packages/package_index.json'
}
[IO.File]::WriteAllText((Join-Path $stage 'sources.json'), ($sources | ConvertTo-Json -Depth 12))
$gcc = 'seed/packages/arduino/tools/avr-gcc/7.3.0-atmel3.6.1-arduino7/bin/'
$files = [ordered]@{}
Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    $relative = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
    $files[$relative] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}
$manifest = [ordered]@{
    formatVersion       = 1;
    id                  = 'arduino.avr';
    version             = '1.8.8';
    host                = 'win-x64';
    compilerId          = 'avr-gcc-7.3.0-arduino7'
    displayName         = 'Arduino AVR Boards 1.8.8 / CLI 1.5.1';
    purpose             = 'arduino'
    componentVersions   = @{ 'arduino-cli' = '1.5.1';
        'arduino:avr'                      = '1.8.8';
        'avr-gcc'                          = '7.3.0-atmel3.6.1-arduino7';
        avrdude                            = '8.0.0-arduino1'
    }
    executables         = @{ 'arduino-cli' = 'arduino-cli.exe';
        gcc                                = ($gcc + 'avr-gcc.exe');
        gxx                                = ($gcc + 'avr-g++.exe');
        objcopy                            = ($gcc + 'avr-objcopy.exe');
        size                               = ($gcc + 'avr-size.exe')
    }
    resourceDirectories = @{ 'arduino-seed' = 'seed' }
    sha256              = $files
}
[IO.File]::WriteAllText((Join-Path $stage 'toolset.json'), ($manifest | ConvertTo-Json -Depth 8))
New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
Move-Item -LiteralPath $stage -Destination $target
Write-Output $target
