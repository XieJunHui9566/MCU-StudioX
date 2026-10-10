param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$PackagingCli
)
$ErrorActionPreference = 'Stop'
$pluginSource = [IO.Path]::GetFullPath($PSScriptRoot)
$installation = [IO.Path]::GetFullPath($InstallDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'OutputDirectory must not already exist.' }
$sdk = Join-Path $installation 'runtime\plugin-host\StudioX.Extensions.Abstractions.dll'
$cli = Join-Path $installation 'runtime\mcp-host\StudioX.Cli.exe'
if ($PackagingCli) { $cli = [IO.Path]::GetFullPath($PackagingCli) }
if (!(Test-Path -LiteralPath $sdk) -or !(Test-Path -LiteralPath $cli)) { throw 'Installed StudioX SDK or CLI is missing.' }
New-Item -ItemType Directory -Path $output | Out-Null
$temporary = Join-Path $output ('.build-' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $temporary 'publish'
$build = Join-Path $temporary 'build'
try {
    & dotnet publish (Join-Path $pluginSource 'StudioX.KeilImporter.csproj') --configuration Release --artifacts-path $build --output $publish --nologo "-p:StudioXSdkPath=$sdk"
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
    Get-ChildItem -LiteralPath $publish -File | Where-Object { $_.Extension -eq '.pdb' -or $_.Name -like 'StudioX.Extensions.Abstractions.*' } | ForEach-Object { Remove-Item -LiteralPath $_.FullName }
    Copy-Item -LiteralPath (Join-Path $pluginSource 'plugin.template.json') -Destination (Join-Path $publish 'plugin.json')
    Copy-Item -LiteralPath (Join-Path $pluginSource 'README.md') -Destination (Join-Path $publish 'README.md')
    $archiveName = 'studiox.keil-importer-0.1.6.studioxplugin'
    $archive = Join-Path $output $archiveName
    & $cli plugin pack $publish $archive
    if ($LASTEXITCODE -ne 0) { throw 'Plugin packaging failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('plugin.json').Open())
        try { $packedManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($packedManifest.scope -ne 'application') { throw 'Packaging CLI does not support application plugins. Use the fixed host CLI via -PackagingCli.' }
    } finally { $zip.Dispose() }
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $archiveName`n", [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $pluginSource 'README.md') -Destination (Join-Path $output 'README.md')
    Write-Output $archive
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    if ([IO.Path]::GetDirectoryName($resolved) -ne $output -or ![IO.Path]::GetFileName($resolved).StartsWith('.build-')) { throw 'Unsafe cleanup target.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
