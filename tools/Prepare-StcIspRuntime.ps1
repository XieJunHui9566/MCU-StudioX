param(
    [Parameter(Mandatory=$true)][string]$PythonEmbedArchive,
    [Parameter(Mandatory=$true)][string]$PackagesDirectory,
    [string]$CompilerExecutable,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/tool-runtime/stc-isp-portable-3.14.7' }
if (!$CompilerExecutable) {
    $command = Get-Command gcc.exe -ErrorAction Stop
    $CompilerExecutable = $command.Source
}
$archivePath = [IO.Path]::GetFullPath($PythonEmbedArchive)
$packages = [IO.Path]::GetFullPath($PackagesDirectory)
$compiler = [IO.Path]::GetFullPath($CompilerExecutable)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$expectedPythonSha256 = 'd297e5ff019966817ad8502465176139f2d3d840fa4ed84b13bed399a6ab1f15'
$pythonUrl = 'https://www.python.org/ftp/python/3.14.7/python-3.14.7-embed-amd64.zip'

if (!(Test-Path -LiteralPath $archivePath -PathType Leaf)) { throw "Missing official Python embeddable archive: $archivePath" }
if (!(Test-Path -LiteralPath $packages -PathType Container)) { throw "Missing installed Python packages: $packages" }
if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw "Missing C compiler: $compiler" }
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory; a prepared ISP runtime is immutable.' }
$actualPythonSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $archivePath).Hash.ToLowerInvariant()
if ($actualPythonSha256 -ne $expectedPythonSha256) { throw "Python archive SHA-256 mismatch: $actualPythonSha256" }

$components = @(
    @{ module='stcgal'; dist='stcgal-1.10.dist-info'; version='1.10'; upstream='https://github.com/grigorig/stcgal' },
    @{ module='serial'; dist='pyserial-3.5.dist-info'; version='3.5'; upstream='https://github.com/pyserial/pyserial/tree/v3.5' },
    @{ module='tqdm'; dist='tqdm-4.67.3.dist-info'; version='4.67.3'; upstream='https://github.com/tqdm/tqdm/tree/v4.67.3' },
    @{ module='colorama'; dist='colorama-0.4.6.dist-info'; version='0.4.6'; upstream='https://github.com/tartley/colorama/tree/0.4.6' }
)
$packageSources = @()
foreach ($component in $components) {
    $source = Join-Path $packages $component.module
    $metadata = Join-Path $packages "$($component.dist)/METADATA"
    if (!(Test-Path -LiteralPath $source -PathType Container) -or !(Test-Path -LiteralPath $metadata -PathType Leaf)) {
        throw "Missing $($component.module) $($component.version) package or metadata."
    }
    $versionLine = [regex]::Match((Get-Content -LiteralPath $metadata -Raw -Encoding utf8), '(?m)^Version:\s*(\S+)\s*$')
    if (!$versionLine.Success -or $versionLine.Groups[1].Value -ne $component.version) {
        throw "Unexpected $($component.module) version; expected $($component.version)."
    }
    $packageSources += [ordered]@{
        name = $component.module
        version = $component.version
        upstream = $component.upstream
        installedMetadataSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $metadata).Hash.ToLowerInvariant()
    }
}
$licenses = Join-Path $projectRoot 'licenses/STC-ISP'
foreach ($name in @('Stcgal-MIT.txt','PySerial-BSD-3-Clause.txt','MPL-2.0.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $licenses $name) -PathType Leaf)) { throw "Missing license: $name" }
}
foreach ($relative in @('tqdm-4.67.3.dist-info/licenses/LICENCE','colorama-0.4.6.dist-info/licenses/LICENSE.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $packages $relative) -PathType Leaf)) { throw "Missing package license: $relative" }
}
$compilerOutput = (& $compiler --version 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or !$compilerOutput) { throw 'Cannot determine native launcher compiler version.' }
$versionOutput = ($compilerOutput -split '\r?\n')[0]

# The official embeddable distribution isolates imports via python314._pth. Copy only
# the interpreter, precompiled standard library, and modules needed by UART stcgal.
$pythonFiles = @(
    'python.exe','python3.dll','python314.dll','python314.zip','python314._pth',
    'vcruntime140.dll','vcruntime140_1.dll','_ctypes.pyd','libffi-8.dll',
    'select.pyd','unicodedata.pyd'
)
[IO.Directory]::CreateDirectory($output) | Out-Null
$zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    foreach ($name in $pythonFiles) {
        $entry = $zip.GetEntry($name)
        if ($null -eq $entry) { throw "Official Python archive is missing: $name" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $output $name))
    }
    $licenseEntry = $zip.GetEntry('LICENSE.txt')
    if ($null -eq $licenseEntry) { throw 'Official Python archive is missing LICENSE.txt' }
    [IO.Directory]::CreateDirectory((Join-Path $output 'licenses')) | Out-Null
    [IO.Compression.ZipFileExtensions]::ExtractToFile($licenseEntry, (Join-Path $output 'licenses/Python-PSF.txt'))
} finally { $zip.Dispose() }

foreach ($component in $components) {
    $source = Join-Path $packages $component.module
    foreach ($file in Get-ChildItem -LiteralPath $source -Recurse -File) {
        if ($file.Extension -ne '.py') { continue }
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in package: $($file.FullName)" }
        $relative = [IO.Path]::GetRelativePath($source, $file.FullName)
        $target = Join-Path $output (Join-Path $component.module $relative)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($file.FullName, $target, $false)
    }
    $metadata = Join-Path $packages "$($component.dist)/METADATA"
    $metadataTarget = Join-Path $output "$($component.dist)/METADATA"
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($metadataTarget)) | Out-Null
    [IO.File]::Copy($metadata, $metadataTarget, $false)
}

foreach ($name in @('Stcgal-MIT.txt','PySerial-BSD-3-Clause.txt','MPL-2.0.txt')) {
    [IO.File]::Copy((Join-Path $licenses $name), (Join-Path $output "licenses/$name"), $false)
}
[IO.File]::Copy((Join-Path $packages 'tqdm-4.67.3.dist-info/licenses/LICENCE'),
    (Join-Path $output 'licenses/Tqdm-LICENCE.txt'), $false)
[IO.File]::Copy((Join-Path $packages 'colorama-0.4.6.dist-info/licenses/LICENSE.txt'),
    (Join-Path $output 'licenses/Colorama-BSD-3-Clause.txt'), $false)

# pip-generated console entry points embed their original Python installation path.
# A tiny native launcher resolves ../python.exe from its own current location.
$launcher = Join-Path $output 'Scripts/stcgal.exe'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($launcher)) | Out-Null
& $compiler -O2 -s -static-libgcc -municode -Wall -Wextra -o $launcher (Join-Path $PSScriptRoot 'stcgal-portable-launcher.c')
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'Portable stcgal launcher compilation failed.' }

# These commands cannot open a COM port. Keep the packaged interpreter isolated
# from the developer's PATH and user-installed Python modules during validation.
$oldPath = $env:PATH
$oldPythonHome = $env:PYTHONHOME
$oldPythonPath = $env:PYTHONPATH
try {
    $env:PATH = "$env:SystemRoot\System32"
    Remove-Item Env:PYTHONHOME -ErrorAction SilentlyContinue
    Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue
    $import = (& (Join-Path $output 'python.exe') -B -c 'import stcgal,serial,tqdm,colorama; print(stcgal.__version__,serial.__version__,tqdm.__version__,colorama.__version__)' 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $import -ne '1.10 3.5 4.67.3 0.4.6') { throw "Bundled Python import check failed: $import" }
    $reported = (& $launcher -V 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $reported -ne 'stcgal 1.10') { throw "Portable launcher version check failed: $reported" }
    $help = (& $launcher --help 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0 -or !$help.Contains('STC MCU ISP flash tool')) { throw 'Portable launcher help check failed.' }
} finally {
    $env:PATH = $oldPath
    if ($null -eq $oldPythonHome) { Remove-Item Env:PYTHONHOME -ErrorAction SilentlyContinue } else { $env:PYTHONHOME = $oldPythonHome }
    if ($null -eq $oldPythonPath) { Remove-Item Env:PYTHONPATH -ErrorAction SilentlyContinue } else { $env:PYTHONPATH = $oldPythonPath }
}

$fileHashes = [ordered]@{}
$bytes = 0L
foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\','/')
    $fileHashes[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $file.FullName).Hash.ToLowerInvariant()
    $bytes += $file.Length
}
[ordered]@{
    formatVersion = 1
    id = 'stc-isp'
    host = 'win-x64'
    pythonVersion = '3.14.7'
    pythonArchive = $pythonUrl
    pythonArchiveSha256 = $actualPythonSha256
    pythonSource = 'https://www.python.org/ftp/python/3.14.7/'
    packages = $packageSources
    launcherSourceSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $PSScriptRoot 'stcgal-portable-launcher.c')).Hash.ToLowerInvariant()
    launcherCompiler = $versionOutput
    notes = 'Official isolated CPython embeddable runtime; only pure Python module sources and required Windows extension DLLs are included. pip launcher and optional USB/PyUSB support are omitted. No COM port was opened during preparation.'
    files = $fileHashes
} | ConvertTo-Json -Depth 9 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8
Write-Output "Prepared portable stcgal 1.10: $($fileHashes.Count) indexed files; $bytes bytes; $output"
