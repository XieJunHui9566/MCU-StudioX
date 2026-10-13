param([string]$OutputDirectory, [string]$BuildArtifactsDirectory, [string]$PackagingCli)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $root 'artifacts/appearance-plugin'
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
& dotnet publish (Join-Path $root 'examples/StudioX.AppearancePlugin/StudioX.AppearancePlugin.csproj') -c Release --self-contained false -o $publish --artifacts-path $build
if ($LASTEXITCODE -ne 0)
{
    throw 'Appearance plugin compilation failed.'
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
$directory = Join-Path $output 'studiox.appearance-studio'
[IO.Directory]::CreateDirectory($directory) | Out-Null
# 契约由插件宿主提供；归档只携带插件自己的程序集和说明。
Copy-Item -LiteralPath (Join-Path $publish 'StudioX.AppearancePlugin.dll') -Destination $directory
Copy-Item -LiteralPath (Join-Path $root 'examples/StudioX.AppearancePlugin/README.md') -Destination $directory
Copy-Item -LiteralPath (Join-Path $root 'examples/StudioX.AppearancePlugin/assets') -Destination $directory -Recurse
Copy-Item -LiteralPath (Join-Path $root 'examples/StudioX.AppearancePlugin/plugin.template.json') -Destination (Join-Path $directory 'plugin.json')
# 归档与内置目录必须使用同一份完整索引；PackAsync 不回写源清单。
$manifest = Get-Content -LiteralPath (Join-Path $directory 'plugin.json') -Raw | ConvertFrom-Json
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse)
{
    if ($file.Name -ne 'plugin.json')
    {
        $relative = [IO.Path]::GetRelativePath($directory, $file.FullName).Replace('\', '/')
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    }
}
$manifest.sha256 = $hashes
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $directory 'plugin.json') -Encoding utf8
$archive = Join-Path $output ('studiox.appearance-studio-' + $manifest.version + '.studioxplugin')
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
    throw 'Appearance plugin packaging failed.'
}
((Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($archive)) |
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Appearance plugin archive: $archive"
