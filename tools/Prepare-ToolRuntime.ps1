param(
    [Parameter(Mandatory=$true)][string]$AgRvDirectory,
    [Parameter(Mandatory=$true)][string]$AgRvOpenOcdDirectory,
    [Parameter(Mandatory=$true)][string]$ArmGccDirectory,
    [Parameter(Mandatory=$true)][string]$RiscVGccDirectory,
    [Parameter(Mandatory=$true)][string]$OpenOcdDirectory,
    [Parameter(Mandatory=$true)][string]$CMakeDirectory,
    [Parameter(Mandatory=$true)][string]$NinjaExecutable,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/tool-runtime' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Choose a new output directory. Existing versioned toolsets are never overwritten.' }
$ninja = [IO.Path]::GetFullPath($NinjaExecutable)
function Version-Line([string]$program, [string]$expected) {
    if (!(Test-Path -LiteralPath $program -PathType Leaf)) { throw "Missing tool: $program" }
    $versionText = (& $program --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $versionText -notmatch [regex]::Escape($expected)) { throw "Tool version mismatch ($expected): $versionText" }
    return ($versionText -split '\r?\n')[0]
}
$cmakeVersion = Version-Line (Join-Path $CMakeDirectory 'bin/cmake.exe') '4.4.0'
$ninjaVersion = Version-Line $ninja '1.10.2'
$recipes = @(
    @{ Id='agm.agrv'; Name='AG32 / AgRV'; Compiler='agrv-gcc-11.1.0'; Prefix='riscv64-unknown-elf'; Gcc=$AgRvDirectory; Version='11.1.0'; Ocd=$AgRvOpenOcdDirectory; OcdVersion='0.12.0+dev-04519-ga93c217e2-dirty'; Scripts='share/openocd/scripts'; Source='AGM AgRV PlatformIO packages toolchain-agrv and tool-agrv_openocd 1.0.0' },
    @{ Id='arm.gnu'; Name='Arm GNU Toolchain'; Compiler='arm-gnu-15.2.rel1'; Prefix='arm-none-eabi'; Gcc=$ArmGccDirectory; Version='15.2.1'; Ocd=$OpenOcdDirectory; OcdVersion='0.12.0+dev-02228-ge5888bda3-dirty'; Scripts='openocd/scripts'; Source='Arm GNU Toolchain 15.2.Rel1; local SysGCC copy (GNU component directories only)' },
    @{ Id='riscv.xpack'; Name='xPack RISC-V GCC'; Compiler='xpack-riscv-gcc-15.2.0'; Prefix='riscv-none-elf'; Gcc=$RiscVGccDirectory; Version='15.2.0'; Ocd=$OpenOcdDirectory; OcdVersion='0.12.0+dev-02228-ge5888bda3-dirty'; Scripts='openocd/scripts'; Source='xPack GNU RISC-V Embedded GCC 15.2.0; local complete runtime copy' }
)
foreach ($recipe in $recipes) {
    $recipe.GccLine = Version-Line (Join-Path $recipe.Gcc "bin/$($recipe.Prefix)-gcc.exe") $recipe.Version
    $recipe.OcdLine = Version-Line (Join-Path $recipe.Ocd 'bin/openocd.exe') $recipe.OcdVersion
    if (!(Test-Path -LiteralPath (Join-Path $recipe.Ocd $recipe.Scripts) -PathType Container)) { throw "OpenOCD scripts missing: $($recipe.Id)" }
}
foreach ($license in @('GNU-GPL-2.txt','GNU-GPL-3.txt','GCC-Runtime-Exception.txt','Ninja-COPYING.txt','Newlib-COPYING.txt')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot "licenses/$license"))) { throw "License text missing: $license" }
}
function Copy-Tree([string]$source, [string]$destination, [string[]]$exclude = @()) {
    $source = [IO.Path]::GetFullPath($source)
    foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point is not allowed in runtime assets: $($item.FullName)" }
        if ($item.PSIsContainer) { continue }
        $relative = [IO.Path]::GetRelativePath($source, $item.FullName).Replace('\','/')
        if ($relative -in $exclude -or $relative.EndsWith('.pyc') -or $relative.Contains('/__pycache__/')) { continue }
        $target = Join-Path $destination $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
        [IO.File]::Copy($item.FullName, $target, $false)
    }
}
New-Item -ItemType Directory -Path $output | Out-Null
foreach ($recipe in $recipes) {
    $bundle = Join-Path $output "toolsets/$($recipe.Id)/1.0.0"
    New-Item -ItemType Directory -Path $bundle -Force | Out-Null
    Write-Output "Preparing $($recipe.Name)..."
    if ($recipe.Id -eq 'arm.gnu') {
        foreach ($component in @('arm-none-eabi','bin','include','lib','libexec','share')) {
            Copy-Tree (Join-Path $recipe.Gcc $component) (Join-Path $bundle "gcc/$component") @('make.exe','ninja.exe')
        }
    } else { Copy-Tree $recipe.Gcc (Join-Path $bundle 'gcc') }
    Copy-Tree $recipe.Ocd (Join-Path $bundle 'openocd') @('STM32F4_stlink.cfg')
    Copy-Tree $CMakeDirectory (Join-Path $bundle 'cmake')
    New-Item -ItemType Directory -Path (Join-Path $bundle 'ninja') | Out-Null
    Copy-Item -LiteralPath $ninja -Destination (Join-Path $bundle 'ninja/ninja.exe')
    New-Item -ItemType Directory -Path (Join-Path $bundle 'licenses') | Out-Null
    foreach ($license in @('GNU-GPL-2.txt','GNU-GPL-3.txt','GCC-Runtime-Exception.txt','Ninja-COPYING.txt','Newlib-COPYING.txt')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "licenses/$license") -Destination (Join-Path $bundle "licenses/$license")
    }
    $roles = [ordered]@{}
    foreach ($role in @('gcc','gxx','gdb','objcopy','objdump','readelf','size','ar','ranlib','as','ld')) {
        $suffix = if ($role -eq 'gxx') { 'g++' } else { $role }
        $relative = "gcc/bin/$($recipe.Prefix)-$suffix.exe"
        if (!(Test-Path -LiteralPath (Join-Path $bundle $relative))) { throw "Tool role missing: $relative" }
        $roles[$role] = $relative
    }
    $roles['cmake'] = 'cmake/bin/cmake.exe'; $roles['ninja'] = 'ninja/ninja.exe'; $roles['openocd'] = 'openocd/bin/openocd.exe'
    [ordered]@{
        source=$recipe.Source; gccVersionOutput=$recipe.GccLine; openOcdVersionOutput=$recipe.OcdLine; cmakeVersionOutput=$cmakeVersion; ninjaVersionOutput=$ninjaVersion
        notes='Local development bundle; original headers, libraries, DLLs, scripts and distribution notices retained. No vendor SDK or user configuration included. Hardware download/debug integration is separate.'
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $bundle 'provenance.json') -Encoding utf8
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $bundle -File -Recurse | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($bundle, $file.FullName).Replace('\','/')
        $hashes[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [ordered]@{
        formatVersion=1; id=$recipe.Id; version='1.0.0'; host='win-x64'; compilerId=$recipe.Compiler; displayName=$recipe.Name
        componentVersions=[ordered]@{ gcc=$recipe.GccLine; openocd=$recipe.OcdLine; cmake=$cmakeVersion; ninja=$ninjaVersion }
        resourceDirectories=@{ openocdScripts="openocd/$($recipe.Scripts)" }; executables=$roles; sha256=$hashes
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $bundle 'toolset.json') -Encoding utf8
    Write-Output "Prepared $($recipe.Id) 1.0.0: $($hashes.Count) indexed files"
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'runtime/THIRD-PARTY-NOTICES.txt') -Destination $output
Write-Output $output
