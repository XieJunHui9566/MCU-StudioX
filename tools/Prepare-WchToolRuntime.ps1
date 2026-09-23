param(
    [Parameter(Mandatory=$true)][string]$WchComponentsDirectory,
    [string]$SharedToolsetDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$SharedToolsetDirectory) { $SharedToolsetDirectory = Join-Path $repo 'artifacts/tool-runtime/toolsets/arm.gnu/1.0.0' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo 'artifacts/tool-runtime/toolsets/wch.riscv/1.0.0' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new directory; versioned toolsets are immutable.' }
$gcc = Join-Path $WchComponentsDirectory 'Toolchain/RISC-V Embedded GCC12'
$ocd = Join-Path $WchComponentsDirectory 'OpenOCD/OpenOCD'
function Version-Line([string]$program, [string]$expected) {
    $value = (& $program --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or !$value.Contains($expected)) { throw "Unexpected tool version: $value" }
    return ($value -split '\r?\n')[0]
}
$gccLine = Version-Line (Join-Path $gcc 'bin/riscv-wch-elf-gcc.exe') '12.2.0'
if ((Get-Content -LiteralPath (Join-Path $gcc 'version.txt') -Raw).Trim() -ne 'v1.4') { throw 'WCH GCC distribution revision changed.' }
$ocdLine = Version-Line (Join-Path $ocd 'bin/openocd.exe') '2026-08-25-16:45'
$cmakeLine = Version-Line (Join-Path $SharedToolsetDirectory 'cmake/bin/cmake.exe') '4.4.0'
$ninjaLine = Version-Line (Join-Path $SharedToolsetDirectory 'ninja/ninja.exe') '1.10.2'
function Copy-Tree([string]$source, [string]$destination) {
    $source = [IO.Path]::GetFullPath($source)
    foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point: $($item.FullName)" }
        if ($item.PSIsContainer) { continue }
        $target = Join-Path $destination ([IO.Path]::GetRelativePath($source, $item.FullName))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($item.FullName, $target, $false)
    }
}
Copy-Tree $gcc (Join-Path $output 'gcc')
Copy-Tree $ocd (Join-Path $output 'openocd')
foreach ($component in @('cmake','ninja','licenses')) {
    Copy-Tree (Join-Path $SharedToolsetDirectory $component) (Join-Path $output $component)
}
$roles = [ordered]@{}
foreach ($role in @('gcc','gxx','gdb','objcopy','objdump','readelf','size','ar','ranlib','as','ld')) {
    $suffix = if ($role -eq 'gxx') { 'g++' } else { $role }
    $relative = "gcc/bin/riscv-wch-elf-$suffix.exe"
    if (!(Test-Path -LiteralPath (Join-Path $output $relative))) { throw "Missing role: $relative" }
    $roles[$role] = $relative
}
$roles.cmake = 'cmake/bin/cmake.exe'; $roles.ninja = 'ninja/ninja.exe'; $roles.openocd = 'openocd/bin/openocd.exe'
$scripts = 'openocd/share/openocd/scripts'
if (!(Test-Path -LiteralPath (Join-Path $output $scripts))) { throw 'Missing OpenOCD scripts.' }
[ordered]@{
    source='MounRiver Studio 2 / WCH components; RISC-V Embedded GCC12 v1.4 and WCH OpenOCD'
    upstream=@('https://www.mounriver.com/','https://github.com/openwch/ch32v307')
    gccVersionOutput=$gccLine; openOcdVersionOutput=$ocdLine; cmakeVersionOutput=$cmakeLine; ninjaVersionOutput=$ninjaLine
    notes='Full vendor distributions retained, including sysroots, multilibs, DLLs and license notices. WCH OpenOCD is bundled for later probe integration; this does not enable hardware flashing/debugging.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'provenance.json') -Encoding utf8
$hashes = [ordered]@{}
foreach ($file in Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($output, $file.FullName).Replace('\','/')
    $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
}
[ordered]@{
    formatVersion=1; id='wch.riscv'; version='1.0.0'; host='win-x64'; compilerId='wch-gcc-12.2.0-v1.4'; displayName='沁恒 WCH RISC-V GCC'
    componentVersions=@{ gcc=$gccLine; openocd=$ocdLine; cmake=$cmakeLine; ninja=$ninjaLine }
    resourceDirectories=@{ openocdScripts=$scripts }; executables=$roles; sha256=$hashes
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'toolset.json') -Encoding utf8
Write-Output "Prepared wch.riscv 1.0.0: $($hashes.Count) indexed files; $output"
