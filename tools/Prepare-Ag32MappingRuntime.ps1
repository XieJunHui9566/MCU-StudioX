param(
    [Parameter(Mandatory = $true)][string]$PlatformDirectory,
    [Parameter(Mandatory = $true)][string]$LogicToolsDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $repo 'artifacts/tool-runtime/toolsets/agm.pin-mapping/1.0.0'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Choose a new directory; versioned toolsets are immutable.'
}
$platform = [IO.Path]::GetFullPath($PlatformDirectory)
$logicTools = [IO.Path]::GetFullPath($LogicToolsDirectory)
$platformManifest = Get-Content -LiteralPath (Join-Path $platform 'platform.json') -Raw | ConvertFrom-Json
$vendor = Get-Content -LiteralPath (Join-Path $logicTools 'package.json') -Raw | ConvertFrom-Json
if ($platformManifest.name -ne 'AgRV' -or $platformManifest.version -ne '1.0.0' -or $vendor.name -ne 'tool-agrv_logic' -or $vendor.version -ne '1.0.0')
{
    throw 'Unexpected AGM platform or Supra distribution; this preparation script targets the verified 1.0.0 vendor package.'
}
function Copy-Tree([string]$source, [string]$destination)
{
    if (([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Reparse point: $source"
    }
    foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -Force)
    {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw "Reparse point: $($item.FullName)"
        }
        if ($item.PSIsContainer)
        {
            continue
        }
        $target = Join-Path $destination ([IO.Path]::GetRelativePath($source, $item.FullName))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($item.FullName, $target, $false)
    }
}
foreach ($file in @('etc/gen_vlog', 'etc/gen_logic.tcl', 'platform.json', 'VERSION', 'README.md'))
{
    $target = Join-Path $output "platform/$file"
    if (([IO.File]::GetAttributes((Join-Path $platform $file)) -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Reparse point: $file"
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::Copy((Join-Path $platform $file), $target, $false)
}
foreach ($component in @('etc', 'lib', 'doc'))
{
    Copy-Tree (Join-Path $logicTools $component) (Join-Path $output "supra/$component")
}
foreach ($file in @('bin/af.exe', 'bin/ftd2xx.dll', 'package.json'))
{
    $target = Join-Path $output "supra/$file"
    if (([IO.File]::GetAttributes((Join-Path $logicTools $file)) -band [IO.FileAttributes]::ReparsePoint) -ne 0)
    {
        throw "Reparse point: $file"
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    [IO.File]::Copy((Join-Path $logicTools $file), $target, $false)
}
Copy-Tree (Join-Path $logicTools 'python_dist') (Join-Path $output 'python')
$pythonVersion = (& (Join-Path $output 'python/python.exe') -I -B --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw "Python failed: $pythonVersion"
}
if ($pythonVersion -ne 'Python 3.11.1')
{
    throw "Unexpected vendor Python runtime: $pythonVersion"
}
# 厂商 license/license.txt 是节点授权数据，不是开源许可声明，禁止进入发行索引。
[ordered]@{
    source          = 'Installed AGM AgRV platform and tool-agrv_logic distribution'
    upstream        = @('https://www.ag32mcu.com/', 'https://www.alta-gate.com/')
    platformVersion = $platformManifest.version
    logicVersion    = $vendor.version
    pythonVersion   = $pythonVersion
    notes           = 'VE converter and direct Supra af.exe invocation only; no Quartus, PlatformIO or shell dependency. Node-locked licenses and all device programmers are excluded.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName)
{
    $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\', '/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[ordered]@{
    formatVersion       = 1;
    id                  = 'agm.pin-mapping';
    version             = '1.0.0';
    host                = 'win-x64';
    compilerId          = 'agm.ve'
    purpose             = 'ag32-mapping';
    displayName         = 'AGM VE pin mapping / Supra'
    executables         = [ordered]@{ python = 'python/python.exe';
        converter                            = 'platform/etc/gen_vlog';
        supra                                = 'supra/bin/af.exe'
    }
    resourceDirectories = [ordered]@{ platform = 'platform/etc';
        supra                                  = 'supra'
    }
    componentVersions   = [ordered]@{ logic = $vendor.version;
        python                              = $pythonVersion
    }
    sha256              = $hashes
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'toolset.json') -Encoding utf8
Write-Output "Prepared $output ($($hashes.Count) indexed files); no license credentials copied."
