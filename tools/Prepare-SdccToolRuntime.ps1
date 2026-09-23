param(
    [Parameter(Mandatory=$true)][string]$SdccDirectory,
    [string]$SharedToolsetDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$SharedToolsetDirectory) { $SharedToolsetDirectory = Join-Path $projectRoot 'artifacts/tool-runtime/toolsets/arm.gnu/1.0.0' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/tool-runtime/toolsets/stc.sdcc/1.0.0' }
$sdcc = [IO.Path]::GetFullPath($SdccDirectory)
$shared = [IO.Path]::GetFullPath($SharedToolsetDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new directory; versioned toolsets are immutable.' }

function Version-Line([string]$program, [string]$expected) {
    if (!(Test-Path -LiteralPath $program -PathType Leaf)) { throw "Missing tool: $program" }
    $value = (& $program --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or !$value.Contains($expected)) { throw "Unexpected tool version ($expected): $value" }
    return ($value -split '\r?\n')[0]
}

# 版本和发行内容先检查完毕，再创建不可覆盖的目标目录。
$sdccLine = Version-Line (Join-Path $sdcc 'bin/sdcc.exe') '4.5.0 #15242 (MINGW64)'
if (!$sdccLine.Contains('mcs51')) { throw 'This SDCC build does not provide the MCS-51 target.' }
$cmakeLine = Version-Line (Join-Path $shared 'cmake/bin/cmake.exe') '4.4.0'
$ninjaLine = Version-Line (Join-Path $shared 'ninja/ninja.exe') '1.10.2'
$roles = [ordered]@{
    sdcc = 'sdcc/bin/sdcc.exe'
    sdar = 'sdcc/bin/sdar.exe'
    sdas8051 = 'sdcc/bin/sdas8051.exe'
    sdld = 'sdcc/bin/sdld.exe'
    sdobjcopy = 'sdcc/bin/sdobjcopy.exe'
    packihx = 'sdcc/bin/packihx.exe'
    makebin = 'sdcc/bin/makebin.exe'
    cmake = 'cmake/bin/cmake.exe'
    ninja = 'ninja/ninja.exe'
}
foreach ($role in @('sdcc','sdar','sdas8051','sdld','sdobjcopy','packihx','makebin')) {
    $name = [IO.Path]::GetFileName($roles[$role])
    if (!(Test-Path -LiteralPath (Join-Path $sdcc "bin/$name") -PathType Leaf)) { throw "Missing SDCC component: $name" }
}
foreach ($component in @('bin','doc','include','lib')) {
    if (!(Test-Path -LiteralPath (Join-Path $sdcc $component) -PathType Container)) { throw "Missing SDCC directory: $component" }
}
foreach ($file in @('COPYING.txt','COPYING3.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $sdcc $file) -PathType Leaf)) { throw "Missing SDCC license: $file" }
}
foreach ($file in @('GNU-GPL-2.txt','GNU-GPL-3.txt','Ninja-COPYING.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $shared "licenses/$file") -PathType Leaf)) { throw "Missing shared license: $file" }
}

function Copy-Tree([string]$source, [string]$destination) {
    $source = [IO.Path]::GetFullPath($source)
    foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point in runtime assets: $($item.FullName)" }
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($source, $item.FullName)
        $target = Join-Path $destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($item.FullName, $target, $false)
    }
}

foreach ($component in @('bin','doc','include','lib')) {
    Copy-Tree (Join-Path $sdcc $component) (Join-Path $output "sdcc/$component")
}
# SDCC non-free 目录只含与 STC 无关的 Microchip PIC 资料，避免把它加入 STC 发行件。
foreach ($file in @('COPYING.txt','COPYING3.txt')) {
    [IO.File]::Copy((Join-Path $sdcc $file), (Join-Path $output "sdcc/$file"), $false)
}
foreach ($component in @('cmake','ninja')) {
    Copy-Tree (Join-Path $shared $component) (Join-Path $output $component)
}
[IO.Directory]::CreateDirectory((Join-Path $output 'licenses')) | Out-Null
foreach ($file in @('GNU-GPL-2.txt','GNU-GPL-3.txt','Ninja-COPYING.txt')) {
    [IO.File]::Copy((Join-Path $shared "licenses/$file"), (Join-Path $output "licenses/$file"), $false)
}

[ordered]@{
    source = 'Locally installed SDCC 4.5.0 Windows x64 binary distribution'
    upstream = @('https://sdcc.sourceforge.net/', 'https://sourceforge.net/projects/sdcc/files/sdcc-win64/4.5.0/')
    sdccVersionOutput = $sdccLine
    cmakeVersionOutput = $cmakeLine
    ninjaVersionOutput = $ninjaLine
    notes = 'Original SDCC bin, include, lib, documentation and COPYING texts retained. The Microchip PIC non-free tree is omitted. CMake and Ninja are copied from the already bundled arm.gnu toolset. This toolset does not enable STC flashing or debugging.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8

$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\','/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[ordered]@{
    formatVersion = 1
    id = 'stc.sdcc'
    version = '1.0.0'
    host = 'win-x64'
    compilerId = 'sdcc-4.5.0-15242'
    displayName = 'STC 8-bit / SDCC'
    componentVersions = [ordered]@{ sdcc = $sdccLine; cmake = $cmakeLine; ninja = $ninjaLine }
    executables = $roles
    sha256 = $hashes
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'toolset.json') -Encoding utf8
Write-Output "Prepared stc.sdcc 1.0.0: $($hashes.Count) indexed files; $output"
