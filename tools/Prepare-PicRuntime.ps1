param([Parameter(Mandatory)][string]$CompilerArchive, [Parameter(Mandatory)][string]$DfpArchive,
    [Parameter(Mandatory)][string]$RuntimeDirectory, [string]$SevenZipPath = 'C:/Program Files/7-Zip/7z.exe')
$ErrorActionPreference = 'Stop'
$compilerHash = '1AB191FD446E1A53EBB039E95AD695C6F18BEF0B2EE3DDD9F08268A46DDDB9F5'
$dfpHash = 'C5C3CE16F908635A6858FC128494B82AE9AA61B7F5AB932A12843BBDFA8F8F62'
if ((Get-FileHash -LiteralPath $CompilerArchive).Hash -ne $compilerHash -or (Get-FileHash -LiteralPath $DfpArchive).Hash -ne $dfpHash)
{
    throw 'Official XC8 or DFP archive checksum differs from the pinned version.'
}
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
$target = Join-Path $runtime 'toolsets/microchip.xc8/4.0.0'
if (Test-Path -LiteralPath $target)
{
    throw 'Use a new component destination; never replace an existing version.'
}
$stage = Join-Path $runtime ('pic-prepare-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try
{
    & $SevenZipPath x '-y' ('-o' + $stage) ([IO.Path]::GetFullPath($CompilerArchive)) *> (Join-Path $stage 'xz.log')
    if ($LASTEXITCODE)
    {
        throw 'XC8 XZ extraction failed'
    }
    $tar = @(Get-ChildItem -LiteralPath $stage -File -Filter '*.tar')
    if ($tar.Count -ne 1)
    {
        throw 'Expected one official compiler TAR payload'
    }
    & $SevenZipPath x '-y' ('-o' + (Join-Path $stage 'compiler')) $tar[0].FullName *> (Join-Path $stage 'tar.log')
    if ($LASTEXITCODE)
    {
        throw 'XC8 TAR extraction failed'
    }
    & $SevenZipPath x '-y' ('-o' + (Join-Path $stage 'dfp')) ([IO.Path]::GetFullPath($DfpArchive)) *> (Join-Path $stage 'dfp.log')
    if ($LASTEXITCODE)
    {
        throw 'DFP extraction failed'
    }
    $compiler = Join-Path $stage 'compiler/xc8-v4.00'
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $compiler 'bin/xc8-cc.exe')
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microchip Technology')
    {
        throw 'XC8 publisher signature is invalid'
    }
    $dfp = Join-Path $stage 'dfp'
    if (!(Test-Path -LiteralPath (Join-Path $dfp 'xc8/pic/include/proc/pic16f877a.h')))
    {
        throw 'DFP does not contain the exact PIC16F877A device'
    }
    $payload = Join-Path $stage 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    Copy-Item -LiteralPath $compiler -Destination (Join-Path $payload 'xc8') -Recurse
    Copy-Item -LiteralPath $dfp -Destination (Join-Path $payload 'dfp') -Recurse
    # 本机私有组件保留官方完整许可；不在此流程生成对外分发的编译器包。
    [ordered]@{ compiler =[ordered]@{version ='4.00';
            url                              ='https://ww1.microchip.com/downloads/aemDocuments/documents/DEV/ProductDocuments/SoftwareTools/xc8-v4.00-full-install-windows-x64.tar.xz';
            sha256                           =$compilerHash
        };
        dfp              =[ordered]@{version ='1.9.176';
            url                              ='https://packs.download.microchip.com/Microchip.PIC16Fxxx_DFP.1.9.176.atpack';
            sha256                           =$dfpHash
        }
    } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'sources.json')
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)
    {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            throw 'Component cannot contain links'
        }
        $hashes[[IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    [ordered]@{formatVersion =1;
        id                   ='microchip.xc8';
        version              ='4.0.0';
        host                 ='win-x64';
        compilerId           ='xc8-4.00-pic';
        displayName          ='Microchip XC8 4.00 / PIC16Fxxx DFP 1.9.176';
        purpose              ='pic-xc8';
        executables          =[ordered]@{'xc8-cc' = 'xc8/bin/xc8-cc.exe' };
        componentVersions    =[ordered]@{xc8 ='4.00';
            PIC16Fxxx_DFP                    ='1.9.176'
        };
        resourceDirectories  =[ordered]@{'pic-dfp' ='dfp/xc8';
            'pic-headers'                          ='xc8/pic/include'
        };
        sha256               =$hashes
    } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $payload 'toolset.json')
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    [IO.Directory]::Move($payload, $target)
}
finally
{
    $resolved = [IO.Path]::GetFullPath($stage)
    if (!$resolved.StartsWith($runtime.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notmatch '^pic-prepare-[a-f0-9]{32}$')
    {
        throw 'Private staging cleanup boundary invalid'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
Get-FileHash -LiteralPath (Join-Path $target 'toolset.json')
