param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$PackagingCli,
    [string]$BuildArtifactsDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = Join-Path $root 'examples/StudioX.MakerPlugins'
$output = [IO.Path]::GetFullPath($OutputDirectory)
$cli = [IO.Path]::GetFullPath($PackagingCli)
if (Test-Path -LiteralPath $output)
{
    throw 'Use a new output directory; existing builds are retained.'
}
if (!(Test-Path -LiteralPath $cli -PathType Leaf))
{
    throw 'Supply the existing activity-aware StudioX packaging CLI.'
}
if (!$BuildArtifactsDirectory)
{
    $BuildArtifactsDirectory = Join-Path $output 'build'
}
$publish = Join-Path $output 'assembly'
# 仅编译新插件及 SDK 契约，不构建、替换或关闭 IDE。
& dotnet publish (Join-Path $source 'StudioX.MakerPlugins.csproj') -c Release --self-contained false -o $publish --artifacts-path ([IO.Path]::GetFullPath($BuildArtifactsDirectory)) --nologo
if ($LASTEXITCODE -ne 0)
{
    throw 'Maker plugins compilation failed.'
}
$packages = Join-Path $output 'StudioX-Makers-1.0.0'
[IO.Directory]::CreateDirectory($packages) | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination $packages
$hashes = [Collections.Generic.List[string]]::new()
foreach ($entry in Get-ChildItem -LiteralPath (Join-Path $source 'manifests') -Filter '*.json' -File)
{
    $manifest = Get-Content -LiteralPath $entry.FullName -Raw | ConvertFrom-Json
    $id = $manifest.id
    if ($id -notmatch '^[a-z0-9]+([.-][a-z0-9]+)*$')
    {
        throw 'Invalid plugin ID.'
    }
    $stage = Join-Path $output $id
    [IO.Directory]::CreateDirectory($stage) | Out-Null
    Copy-Item -LiteralPath (Join-Path $publish 'StudioX.MakerPlugins.dll') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination $stage
    Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $stage 'plugin.json')
    $archive = Join-Path $packages "$id-$($manifest.version).studioxplugin"
    if ([IO.Path]::GetExtension($cli) -eq '.dll')
    {
        & dotnet $cli plugin pack $stage $archive
    }
    else
    {
        & $cli plugin pack $stage $archive
    }
    if ($LASTEXITCODE -ne 0)
    {
        throw "Packaging failed: $id"
    }
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try
    {
        $reader = [IO.StreamReader]::new($zip.GetEntry('plugin.json').Open())
        try
        {
            $packed = $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally
        {
            $reader.Dispose()
        }
        if (($packed.activity | ConvertTo-Json -Depth 12 -Compress) -ne ($manifest.activity | ConvertTo-Json -Depth 12 -Compress))
        {
            # 序列化可能补充 null 字段，按有意义的字段比较。
            if ($packed.activity.version -ne $manifest.activity.version -or $packed.activity.title -ne $manifest.activity.title -or
                $packed.activity.tooltip -ne $manifest.activity.tooltip -or
                ($packed.activity.icon.strokes | ConvertTo-Json -Depth 12 -Compress) -ne ($manifest.activity.icon.strokes | ConvertTo-Json -Depth 12 -Compress))
            {
                throw 'Packaging CLI discarded plugin activity metadata.'
            }
        }
    }
    finally
    {
        $zip.Dispose()
    }
    $hashes.Add((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($archive))
}
$hashes | Set-Content -LiteralPath (Join-Path $packages 'SHA256SUMS.txt') -Encoding ascii
Compress-Archive -LiteralPath $packages -DestinationPath (Join-Path $output 'StudioX-Makers-1.0.0.zip')
Write-Output "Four independent plugin packages: $packages"
