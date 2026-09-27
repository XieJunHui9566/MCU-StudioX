param(
    [Parameter(Mandatory = $true)][string]$MinGWDirectory,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $repo 'artifacts/tool-runtime/toolsets/pc.mingw/1.0.0'
}
$sourceRoot = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $MinGWDirectory).Path).TrimEnd('\')
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\')
$sourcePrefix = $sourceRoot + [IO.Path]::DirectorySeparatorChar
if ($outputRoot.Equals($sourceRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $outputRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase) -or
    !$outputRoot.Contains([IO.Path]::DirectorySeparatorChar))
{
    throw 'PC tool output must be outside the source distribution.'
}

function Assert-Contained([string]$root, [string]$path)
{
    $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($path)
    if (!$resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Path outside the intended directory: $resolved"
    }
    return $resolved
}
function File-Sha([string]$path)
{
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Text-Sha([string]$value)
{
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($value))).ToLowerInvariant()
}
function Invoke-PcTool([string]$program, [string[]]$toolArguments, [string]$workingDirectory, [string]$gccRoot, [string]$inputText = '')
{
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $program;
    $start.WorkingDirectory = $workingDirectory
    $start.UseShellExecute = $false;
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true;
    $start.RedirectStandardError = $true;
    $start.RedirectStandardInput = $true
    foreach ($item in $toolArguments)
    {
        $start.ArgumentList.Add($item)
    }
    foreach ($name in @('GCC_EXEC_PREFIX', 'COMPILER_PATH', 'LIBRARY_PATH', 'CPATH', 'C_INCLUDE_PATH', 'CPLUS_INCLUDE_PATH', 'OBJC_INCLUDE_PATH', 'CMAKE_PREFIX_PATH', 'CMAKE_TOOLCHAIN_FILE', 'INCLUDE', 'LIB', 'LIBPATH', 'CC', 'CXX', 'AR', 'LD', 'PKG_CONFIG_PATH', 'PYTHONHOME', 'PYTHONPATH'))
    {
        $start.Environment.Remove($name) | Out-Null
    }
    $start.Environment['PATH'] = (Join-Path $gccRoot 'bin') + ';' + (Join-Path $env:SystemRoot 'System32') + ';' + $env:SystemRoot
    $process = [Diagnostics.Process]::new();
    $process.StartInfo = $start
    try
    {
        $process.Start() | Out-Null
        $stdout = $process.StandardOutput.ReadToEndAsync();
        $stderr = $process.StandardError.ReadToEndAsync()
        if ($inputText)
        {
            $process.StandardInput.Write($inputText)
        };
        $process.StandardInput.Close()
        if (!$process.WaitForExit(60000))
        {
            $process.Kill($true);
            throw "PC tool timed out: $([IO.Path]::GetFileName($program))"
        }
        $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0)
        {
            throw "PC tool failed ($($process.ExitCode)): $([IO.Path]::GetFileName($program))`n$text"
        }
        return $text.Trim()
    }
    finally
    {
        $process.Dispose()
    }
}
$gccVersion = Invoke-PcTool (Join-Path $sourceRoot 'bin/gcc.exe') @('-dumpfullversion') $sourceRoot $sourceRoot
$target = Invoke-PcTool (Join-Path $sourceRoot 'bin/gcc.exe') @('-dumpmachine') $sourceRoot $sourceRoot
if ($gccVersion -ne '13.1.0' -or $target -ne 'x86_64-w64-mingw32')
{
    throw "Expected MinGW GCC 13.1.0 for x86_64-w64-mingw32; got $gccVersion / $target"
}
$buildInfo = Join-Path $sourceRoot 'build-info.txt'
if (!(Test-Path -LiteralPath $buildInfo -PathType Leaf))
{
    throw 'The original MinGW distribution build-info.txt is required.'
}
$originalBuildInfo = Get-Content -LiteralPath $buildInfo -Raw
if ($originalBuildInfo -notmatch '--mode=gcc-13\.1\.0' -or $originalBuildInfo -notmatch '--rt-version=v11' -or
    $originalBuildInfo -notmatch '--threads=win32' -or $originalBuildInfo -notmatch '--exceptions=seh')
{
    throw 'Unexpected MinGW distribution build configuration.'
}
$inventory = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force)
if ($inventory | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
{
    throw 'Reparse points are not accepted in the source distribution.'
}
$allFiles = @($inventory | Where-Object { !$_.PSIsContainer })
$binNames = @('gcc.exe', 'g++.exe', 'cpp.exe', 'ar.exe', 'ranlib.exe', 'as.exe', 'ld.exe', 'ld.bfd.exe', 'objcopy.exe', 'objdump.exe', 'size.exe', 'nm.exe', 'readelf.exe', 'strip.exe', 'gcc-ar.exe', 'gcc-nm.exe', 'gcc-ranlib.exe', 'dlltool.exe', 'windres.exe', 'windmc.exe', 'c++filt.exe')
$selected = [Collections.Generic.Dictionary[string, IO.FileInfo]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($file in $allFiles)
{
    $relative = [IO.Path]::GetRelativePath($sourceRoot, $file.FullName).Replace('\', '/')
    $keep = $relative -eq 'build-info.txt' -or
    ($relative.StartsWith('bin/') -and ($file.Name -in $binNames -or ($file.Extension -eq '.dll' -and $file.Name -notin @('libgfortran-5.dll', 'libquadmath-0.dll')))) -or
    (($relative -match '^(include|lib|libexec|x86_64-w64-mingw32|licenses)/') -and
    $relative -notmatch '(?i)(f951\.exe|(^|/)(libgfortran[^/]*|libcaf_single\.a|libquadmath[^/]*|gfortran[^/]*|[^/]*\.mod$)|/install-tools/|\.py$|\.pyc$)')
    if ($keep)
    {
        $selected.Add($relative, $file)
    }
}
foreach ($required in @('bin/gcc.exe', 'bin/g++.exe', 'bin/ar.exe', 'bin/ranlib.exe', 'bin/as.exe', 'bin/ld.exe', 'bin/objcopy.exe', 'bin/objdump.exe', 'bin/size.exe',
        'libexec/gcc/x86_64-w64-mingw32/13.1.0/cc1.exe', 'libexec/gcc/x86_64-w64-mingw32/13.1.0/cc1plus.exe', 'libexec/gcc/x86_64-w64-mingw32/13.1.0/lto1.exe',
        'lib/gcc/x86_64-w64-mingw32/13.1.0/libstdc++.a', 'x86_64-w64-mingw32/include/windows.h',
        'licenses/gcc/COPYING3', 'licenses/gcc/COPYING.RUNTIME', 'licenses/binutils/COPYING3', 'licenses/mingw-w64/COPYING.MinGW-w64-runtime.txt'))
{
    if (!$selected.ContainsKey($required))
    {
        throw "Required C/C++ distribution resource missing: $required"
    }
}
# 原始 objdump 检查每个保留的 PE 文件；只允许系统 DLL 或明确纳入的原始 DLL，不借用宿主 PATH。
$imports = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$checked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
do
{
    $pending = @($selected.Keys | Where-Object { $_ -match '\.(exe|dll)$' -and !$checked.Contains($_) })
    foreach ($relative in $pending)
    {
        $description = Invoke-PcTool (Join-Path $sourceRoot 'bin/objdump.exe') @('-p', $selected[$relative].FullName) $sourceRoot $sourceRoot
        foreach ($match in [regex]::Matches($description, 'DLL Name:\s*([^\r\n]+)'))
        {
            $dll = $match.Groups[1].Value.Trim()
            if ([IO.Path]::GetFileName($dll) -ne $dll)
            {
                throw "Invalid PE DLL import: $dll"
            }
            $imports.Add($dll) | Out-Null
            $distributionDll = 'bin/' + $dll
            if ($selected.ContainsKey($distributionDll))
            {
                continue
            }
            $sourceDll = Join-Path $sourceRoot $distributionDll
            if (Test-Path -LiteralPath $sourceDll -PathType Leaf)
            {
                $selected.Add($distributionDll, [IO.FileInfo]::new($sourceDll));
                continue
            }
            if (!(Test-Path -LiteralPath (Join-Path $env:SystemRoot "System32/$dll") -PathType Leaf) -and $dll -notmatch '^(api|ext)-ms-win-')
            {
                throw "Unresolved PC compiler DLL dependency: $dll"
            }
        }
        $checked.Add($relative) | Out-Null
    }
} while ($pending.Count -gt 0)
$sourceHashes = [ordered]@{}
foreach ($relative in $selected.Keys | Sort-Object)
{
    $sourceHashes[$relative] = File-Sha $selected[$relative].FullName
}
$sourceFingerprint = Text-Sha (($sourceHashes.GetEnumerator() | ForEach-Object { $_.Key + '=' + $_.Value }) -join "`n")
$selectedBytes = ($selected.Values | Measure-Object Length -Sum).Sum
Write-Output ("PC C/C++ bundle: {0:N1} MiB / {1} selected files; source {2:N1} MiB. CMake/Ninja remain shared." -f ($selectedBytes / 1MB), $selected.Count, (($allFiles | Measure-Object Length -Sum).Sum / 1MB))
if (Test-Path -LiteralPath $outputRoot)
{
    $manifestPath = Join-Path $outputRoot 'toolset.json';
    $provenancePath = Join-Path $outputRoot 'provenance.json'
    if (!(Test-Path -LiteralPath $manifestPath -PathType Leaf) -or !(Test-Path -LiteralPath $provenancePath -PathType Leaf))
    {
        throw 'Existing PC bundle is incomplete; it will not be overwritten.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    $provenance = Get-Content -LiteralPath $provenancePath -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.id -ne 'pc.mingw' -or $manifest.version -ne '1.0.0' -or $manifest.purpose -ne 'windows-native' -or $manifest.compilerId -ne 'mingw-gcc-13.1.0' -or
        $manifest.componentVersions.gcc -ne '13.1.0' -or $manifest.componentVersions.gxx -ne '13.1.0' -or
        $provenance.selectedSourceFingerprint -ne $sourceFingerprint)
    {
        throw 'Existing PC bundle differs from this source; versioned output will not be overwritten.'
    }
    $existingEntries = @(Get-ChildItem -LiteralPath $outputRoot -Recurse -Force)
    if ($existingEntries | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
    {
        throw 'Existing PC bundle contains a reparse point.'
    }
    $existingFiles = @($existingEntries | Where-Object { !$_.PSIsContainer -and $_.FullName -ne $manifestPath })
    if ($existingFiles.Count -ne $manifest.sha256.Count)
    {
        throw 'Existing PC bundle has missing or extra files.'
    }
    foreach ($file in $existingFiles)
    {
        $relative = [IO.Path]::GetRelativePath($outputRoot, $file.FullName).Replace('\', '/')
        if (!$manifest.sha256.ContainsKey($relative) -or (File-Sha $file.FullName) -ne $manifest.sha256[$relative])
        {
            throw "Existing PC bundle hash mismatch: $relative"
        }
    }
    Write-Output "Verified and reused pc.mingw 1.0.0: $($manifest.sha256.Count) indexed files; $outputRoot"
    return
}
$parent = [IO.Path]::GetDirectoryName($outputRoot)
$stage = Assert-Contained $parent ($outputRoot + '.prepare')
if (Test-Path -LiteralPath $stage)
{
    throw 'Preparation directory already exists; it will not be overwritten.'
}
$stageCreated = $false
try
{
    [IO.Directory]::CreateDirectory($stage) | Out-Null;
    $stageCreated = $true
    foreach ($relative in $sourceHashes.Keys)
    {
        $destination = Assert-Contained $stage (Join-Path $stage ('gcc/' + $relative))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($selected[$relative].FullName, $destination, $false)
        if ((File-Sha $destination) -ne $sourceHashes[$relative])
        {
            throw "Source changed during preparation: $relative"
        }
    }
    $preparedGcc = Join-Path $stage 'gcc'
    $cProbe = Join-Path $stage '.verify-c.exe';
    $cppProbe = Join-Path $stage '.verify-cpp.exe'
    $cText = @'
#include <windows.h>
#include <stdio.h>
int main(void) { if (!GetModuleHandleW(NULL)) return 2; puts("STUDIOX_PC_C_OK"); return 0; }
'@
    $cppText = @'
#include <windows.h>
#include <cstdio>
#include <vector>
#include <string>
#include <filesystem>
#include <numeric>
#include <stdexcept>
int main() {
    std::vector<int> values{1, 2, 3};
    if (std::accumulate(values.begin(), values.end(), 0) != 6 || !GetModuleHandleW(nullptr)) return 3;
    std::filesystem::path name("studiox/probe.cpp");
    if (name.filename().string() != "probe.cpp") return 4;
    try { throw std::runtime_error("stdlib exception"); }
    catch (const std::exception& e) { if (std::string(e.what()) != "stdlib exception") return 5; }
    std::puts("STUDIOX_PC_CPP_OK"); return 0;
}
'@
    Invoke-PcTool (Join-Path $preparedGcc 'bin/gcc.exe') @('-std=c17', '-O2', '-flto', '-x', 'c', '-', '-o', $cProbe) $stage $preparedGcc $cText | Out-Null
    $cResult = Invoke-PcTool $cProbe @() $stage $preparedGcc
    Invoke-PcTool (Join-Path $preparedGcc 'bin/g++.exe') @('-std=c++17', '-O2', '-flto', '-x', 'c++', '-', '-o', $cppProbe) $stage $preparedGcc $cppText | Out-Null
    $cppResult = Invoke-PcTool $cppProbe @() $stage $preparedGcc
    if ($cResult -ne 'STUDIOX_PC_C_OK' -or $cppResult -ne 'STUDIOX_PC_CPP_OK')
    {
        throw 'Prepared compiler C/C++ runtime probe failed.'
    }
    foreach ($probe in @($cProbe, $cppProbe))
    {
        $verified = Assert-Contained $stage $probe;
        Remove-Item -LiteralPath $verified -Force
    }
    $noticeSource = Join-Path $repo 'licenses/PC-Toolchain-NOTICE.md'
    if (!(Test-Path -LiteralPath $noticeSource -PathType Leaf))
    {
        throw 'PC redistribution notice missing.'
    }
    [IO.Directory]::CreateDirectory((Join-Path $stage 'licenses')) | Out-Null
    Copy-Item -LiteralPath $noticeSource -Destination (Join-Path $stage 'licenses/PC-Toolchain-NOTICE.md')
    $gccLine = Invoke-PcTool (Join-Path $preparedGcc 'bin/gcc.exe') @('--version') $stage $preparedGcc
    $gxxLine = Invoke-PcTool (Join-Path $preparedGcc 'bin/g++.exe') @('--version') $stage $preparedGcc
    $binutilsLine = Invoke-PcTool (Join-Path $preparedGcc 'bin/ld.exe') @('--version') $stage $preparedGcc
    $sourceUrls = @([regex]::Matches($originalBuildInfo, '(?m)^url\s*:\s*(\S+)') | ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
    [ordered]@{
        formatVersion                    =1;
        distribution                     ='MinGW-W64-builds 5.0.0 / x86_64-13.1.0-release-win32-seh-msvcrt-rt_v11-rev1'
        upstream                         =@('https://github.com/niXman/mingw-builds-binaries/releases/tag/13.1.0-rt_v11-rev1', 'https://github.com/niXman/mingw-builds', 'https://gcc.gnu.org/', 'https://www.mingw-w64.org/', 'https://www.gnu.org/software/binutils/')
        sourceUrlsRecordedByDistribution =$sourceUrls;
        originalBuildInfoSha256          =(File-Sha $buildInfo)
        selectedSourceFingerprint        =$sourceFingerprint;
        selectedSourceFileCount          =$selected.Count;
        selectedSourceBytes              =$selectedBytes
        sourceFingerprintFormat          ='SHA-256 of UTF-8 sorted relativePath=sha256 entries joined by LF, without trailing LF'
        target                           =$target;
        gccVersionOutput                 =($gccLine -split '\r?\n')[0];
        gxxVersionOutput                 =($gxxLine -split '\r?\n')[0];
        binutilsVersionOutput            =($binutilsLine -split '\r?\n')[0]
        peImports                        =@($imports | Sort-Object);
        validation                       =@{ c =$cResult;
            cpp                                =$cppResult;
            lto                                =$true;
            path                               ='Only prepared gcc/bin and Windows system directories; compiler/include/library override variables removed.'
        }
        retained                         ='C/C++ drivers, cc1/cc1plus, collect2/LTO, GNU assembler/linker/archive tools, complete C/C++/Windows headers/import libraries/startup resources, required runtime DLLs, original build-info and license tree.'
        excluded                         ='GDB/Python runtime, Fortran front-end/libraries, documentation/translations, install-tools and duplicate driver aliases. No CMake/Ninja copy; StudioX reuses its bundled build system.'
        redistribution                   ='Local installed binary distribution copied without binary modifications; original source/build references retained. Upstream links and this inventory do not replace GPL Corresponding Source delivery requirements for public redistribution.'
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'provenance.json') -Encoding utf8
    $roles = [ordered]@{ gcc ='gcc/bin/gcc.exe';
        gxx                  ='gcc/bin/g++.exe';
        ar                   ='gcc/bin/ar.exe';
        ranlib               ='gcc/bin/ranlib.exe';
        as                   ='gcc/bin/as.exe';
        ld                   ='gcc/bin/ld.exe';
        objcopy              ='gcc/bin/objcopy.exe';
        objdump              ='gcc/bin/objdump.exe';
        size                 ='gcc/bin/size.exe';
        cpp                  ='gcc/bin/cpp.exe';
        nm                   ='gcc/bin/nm.exe';
        readelf              ='gcc/bin/readelf.exe';
        strip                ='gcc/bin/strip.exe';
        windres              ='gcc/bin/windres.exe';
        dlltool              ='gcc/bin/dlltool.exe';
        gccAr                ='gcc/bin/gcc-ar.exe'
    }
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName)
    {
        $relative = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/')
        $hashes[$relative] = File-Sha $file.FullName
    }
    [ordered]@{ formatVersion =1;
        id                    ='pc.mingw';
        version               ='1.0.0';
        host                  ='win-x64';
        compilerId            ='mingw-gcc-13.1.0';
        purpose               ='windows-native'
        displayName           ='Windows PC GCC / G++ 13.1.0';
        componentVersions     =@{ gcc =$gccVersion;
            gxx                       =$gccVersion;
            binutils                  =($binutilsLine -split '\r?\n')[0]
        }
        executables           =$roles;
        sha256                =$hashes
    } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $stage 'toolset.json') -Encoding utf8
    $resolvedStage = Assert-Contained $parent $stage
    $resolvedOutput = Assert-Contained $parent $outputRoot
    Move-Item -LiteralPath $resolvedStage -Destination $resolvedOutput
    $stageCreated = $false
    Write-Output "Prepared pc.mingw 1.0.0: $($hashes.Count) indexed files; C/C++ + LTO/runtime checks passed; $outputRoot"
}
finally
{
    # 失败时只移除本次创建的固定 staging，不能清理旧目录或本机原始工具链。
    if ($stageCreated -and (Test-Path -LiteralPath $stage))
    {
        $cleanup = Assert-Contained $parent $stage
        if ($cleanup -ne $outputRoot + '.prepare')
        {
            throw 'Preparation cleanup target changed.'
        }
        Remove-Item -LiteralPath $cleanup -Recurse -Force
    }
}
