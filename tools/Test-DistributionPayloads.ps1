param(
    [Parameter(Mandatory = $true)][string]$FullPayloadDirectory,
    [Parameter(Mandatory = $true)][string]$LightPayloadDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Distribution-Profile.ps1')
$full = [IO.Path]::GetFullPath($FullPayloadDirectory)
$light = [IO.Path]::GetFullPath($LightPayloadDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Use a new validation directory.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Passed, [string]$Description)
{
    if (!$Passed)
    {
        throw $Description
    }
    $checks.Add($Description)
    Write-Output "PASS $Description"
}
$fullRelease = Get-Content -LiteralPath (Join-Path $full 'release.json') -Raw | ConvertFrom-Json
$lightRelease = Get-Content -LiteralPath (Join-Path $light 'release.json') -Raw | ConvertFrom-Json
Check ($fullRelease.distributionProfile -eq 'full' -and $lightRelease.distributionProfile -eq 'light' -and
    $fullRelease.version -eq $lightRelease.version -and $fullRelease.sourceCommit -eq $lightRelease.sourceCommit -and
    $fullRelease.platform -eq 'win-x64' -and $lightRelease.platform -eq 'win-x64') 'both payloads retain exact product and source identity'
Check ($fullRelease.bundledPlugins -eq $lightRelease.bundledPlugins) 'both payloads use the same bundled plugin policy'
foreach ($payload in @($full, $light))
{
    $record = Get-Content -LiteralPath (Join-Path $payload 'release.json') -Raw | ConvertFrom-Json
    foreach ($entry in @(@('development-components.json', 'componentInventorySha256'), @('device-packs/index.json', 'devicePackCatalogSha256')))
    {
        Check ((Get-FileHash -LiteralPath (Join-Path $payload $entry[0])).Hash -eq $record.($entry[1])) "$($record.distributionProfile) metadata hash matches $($entry[0])"
    }
    $inventory = Get-Content -LiteralPath (Join-Path $payload 'development-components.json') -Raw | ConvertFrom-Json
    $actual = @(Get-StudioXBundledComponents $payload)
    Check ($inventory.formatVersion -eq 1 -and $actual.Count -eq $inventory.components.Count -and $actual.Count -eq $record.bundledDevelopmentComponents) "$($record.distributionProfile) inventory count matches installed manifest identities"
    foreach ($component in $actual)
    {
        $item = @($inventory.components | Where-Object { $_.id -eq $component.id -and $_.version -eq $component.version })
        if ($item.Count -ne 1 -or $item[0].host -ne $component.host -or $item[0].compilerId -ne $component.compilerId -or $item[0].fingerprint -ne $component.fingerprint -or $item[0].directory -ne $component.directory)
        {
            throw 'Inventory identity or raw manifest fingerprint mismatch.'
        }
    }
}
Check ($fullRelease.bundledDevelopmentComponents -gt 0 -and $lightRelease.bundledDevelopmentComponents -eq 0) 'full bundles components and light bundles none'
foreach ($relative in @('runtime/toolsets', 'runtime/hdl', 'runtime/stc-isp'))
{
    Check (!(Test-Path -LiteralPath (Join-Path $light $relative))) "light contains no $relative"
}
foreach ($relative in @('MCU StudioX.exe', 'coreclr.dll', 'hostfxr.dll', 'runtime/languages/clangd/bin/clangd.exe',
        'runtime/git/cmd/git.exe', 'runtime/plugin-host/StudioX.PluginHost.exe', 'runtime/mcp-host/StudioX.Cli.exe'))
{
    Check (Test-Path -LiteralPath (Join-Path $light $relative) -PathType Leaf) "light retains $relative"
}
$fullPacks = @(Get-Content -LiteralPath (Join-Path $full 'device-packs/index.json') -Raw | ConvertFrom-Json)
$lightPacks = @(Get-Content -LiteralPath (Join-Path $light 'device-packs/index.json') -Raw | ConvertFrom-Json)
# 目录 JSON 的属性顺序不构成身份；归档文件仍逐字节比较并核对各自索引。
$fields = @('file', 'id', 'version', 'sha256', 'devices', 'framework', 'sdkVersion', 'target', 'provenanceSha256')
$left = $fullPacks | Sort-Object id, version, file | Select-Object $fields | ConvertTo-Json -Depth 20 -Compress
$right = $lightPacks | Sort-Object id, version, file | Select-Object $fields | ConvertTo-Json -Depth 20 -Compress
Check ($fullPacks.Count -gt 0 -and $left -ceq $right) 'full and light retain identical device pack identities, metadata and hashes'
foreach ($pack in $lightPacks)
{
    if (!$pack.file -or $pack.file -match '(^|[\\/])\.\.([\\/]|$)|:|^[\\/]' -or [IO.Path]::GetExtension($pack.file) -ne '.mcupack')
    {
        throw 'Invalid pack index path.'
    }
    foreach ($payload in @($full, $light))
    {
        if ((Get-FileHash -LiteralPath (Join-Path (Join-Path $payload 'device-packs') $pack.file)).Hash -ne $pack.sha256)
        {
            throw "Device pack content changed: $($pack.id)"
        }
    }
}
$checks.Add('every device pack matches the exact declared archive hash in both payloads')
function Get-CommonFiles([string]$Root)
{
    $stack = [Collections.Generic.Stack[string]]::new()
    $stack.Push($Root)
    $files = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($stack.Count)
    {
        $directory = $stack.Pop()
        Assert-StudioXReleaseDirectory $directory
        foreach ($file in [IO.Directory]::EnumerateFiles($directory))
        {
            $relative = [IO.Path]::GetRelativePath($Root, $file).Replace('\', '/')
            if ($relative -in @('release.json', 'development-components.json', 'device-packs/index.json'))
            {
                continue
            }
            if ((Get-Item -LiteralPath $file -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)
            {
                throw 'Linked payload file.'
            }
            $files.Add($relative, $file)
        }
        foreach ($child in [IO.Directory]::EnumerateDirectories($directory))
        {
            $relative = [IO.Path]::GetRelativePath($Root, $child).Replace('\', '/')
            if ($relative -in @('runtime/toolsets', 'runtime/hdl', 'runtime/stc-isp'))
            {
                continue
            }
            $stack.Push($child)
        }
    }
    return , $files
}
$fullFiles = Get-CommonFiles $full
$lightFiles = Get-CommonFiles $light
if ($fullFiles.Count -ne $lightFiles.Count)
{
    Compare-Object @($fullFiles.Keys) @($lightFiles.Keys) | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'different-file-sets.json')
}
Check ($fullFiles.Count -eq $lightFiles.Count) 'both payloads contain the same complete set of common files'
$mismatches = [Collections.Generic.List[string]]::new()
foreach ($relative in $fullFiles.Keys)
{
    if (!$lightFiles.ContainsKey($relative) -or
        (Get-FileHash -LiteralPath $fullFiles[$relative]).Hash -ne (Get-FileHash -LiteralPath $lightFiles[$relative]).Hash)
    {
        $mismatches.Add($relative)
    }
}
if ($mismatches.Count)
{
    $mismatches | Set-Content -LiteralPath (Join-Path $output 'mismatches.txt');
    throw "Common content differs in $($mismatches.Count) files."
}
$checks.Add('all common IDE, runtime, plugin, language, Git, pack and document files match byte for byte')
@{success          =$true;
    version        =$fullRelease.version;
    sourceCommit   =$fullRelease.sourceCommit;
    formalRelease  =$false;
    fullComponents =$fullRelease.bundledDevelopmentComponents;
    devicePacks    =$lightPacks.Count;
    commonFiles    =$fullFiles.Count;
    checks         =$checks
} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
Write-Output "$($checks.Count) payload checks passed; $($fullFiles.Count) common files and $($lightPacks.Count) device packs."
