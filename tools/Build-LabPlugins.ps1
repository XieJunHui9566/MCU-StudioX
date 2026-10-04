param([string]$OutputDirectory, [string]$BuildArtifactsDirectory, [string]$PackagingCli)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $root 'artifacts/lab-plugins'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Use a new output directory; existing plugin builds are retained.'
}
if (!$BuildArtifactsDirectory)
{
    $BuildArtifactsDirectory = Join-Path $output 'build'
}
$build = [IO.Path]::GetFullPath($BuildArtifactsDirectory)
$publish = Join-Path $output 'assembly'
& dotnet publish (Join-Path $root 'examples/StudioX.LabPlugins/StudioX.LabPlugins.csproj') -c Release --self-contained false -o $publish --artifacts-path $build
if ($LASTEXITCODE -ne 0)
{
    throw 'Lab plugin compilation failed.'
}
if (!$PackagingCli)
{
    & dotnet build (Join-Path $root 'src/StudioX.Cli/StudioX.Cli.csproj') -c Release --artifacts-path $build --nologo
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Plugin packaging CLI compilation failed.'
    }
    $PackagingCli = Join-Path $build 'bin/StudioX.Cli/release_win-x64/StudioX.Cli.dll'
}
$cli = [IO.Path]::GetFullPath($PackagingCli)
if (!(Test-Path -LiteralPath $cli -PathType Leaf))
{
    throw 'Packaging CLI not found.'
}
$catalog = Get-ChildItem -LiteralPath (Join-Path $root 'examples/StudioX.LabPlugins/manifests') -Filter '*.json' -File
$hashes = [Collections.Generic.List[string]]::new()
foreach ($entry in $catalog)
{
    $manifest = Get-Content -LiteralPath $entry.FullName -Raw | ConvertFrom-Json
    $id = $manifest.id
    if ($id -notmatch '^[a-z0-9]+([.-][a-z0-9]+)*$')
    {
        throw 'Invalid plugin ID.'
    }
    $directory = Join-Path $output $id
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    # 每个包只交付插件自身程序集与文档；契约由独立宿主提供，不复制 IDE 核心或全局运行时。
    Copy-Item -LiteralPath (Join-Path $publish 'StudioX.LabPlugins.dll') -Destination $directory
    Copy-Item -LiteralPath (Join-Path $root 'examples/StudioX.LabPlugins/README.md') -Destination $directory
    Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $directory 'plugin.json')
    $archive = Join-Path $output "$id-$($manifest.version).studioxplugin"
    if ([IO.Path]::GetExtension($cli) -eq '.dll')
    {
        & dotnet $cli plugin pack $directory $archive
    }
    else
    {
        & $cli plugin pack $directory $archive
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
        if ($packed.activity.version -ne $manifest.activity.version -or $packed.activity.title -ne $manifest.activity.title -or
            $packed.activity.tooltip -ne $manifest.activity.tooltip -or
            ($packed.activity.icon.strokes | ConvertTo-Json -Depth 12 -Compress) -ne ($manifest.activity.icon.strokes | ConvertTo-Json -Depth 12 -Compress))
        {
            throw 'Packaging CLI discarded activity metadata; use the CLI supplied with the generic activity interface.'
        }
    }
    finally
    {
        $zip.Dispose()
    }
    $hashes.Add(((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($archive)))
}
$hashes | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "$($catalog.Count) plugin archives: $output"
