param([Parameter(Mandatory)][string]$IpeDirectory, [Parameter(Mandatory)][string]$RuntimeDirectory,
    [Parameter(Mandatory)][string]$InstallerArchive)
$ErrorActionPreference = 'Stop'
# 开发侧提取官方运行组件。产品使用此私有目录，不查注册表、系统 Java 或 MPLAB 安装目录。
$source = [IO.Path]::GetFullPath($IpeDirectory)
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
$installerHash = '28DB195FB4277853A42CF34DA7A6F68A6453B77ED8530C9D9499E4CC67D17782'
$signature = Get-AuthenticodeSignature -LiteralPath $InstallerArchive
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microchip Technology' -or
    (Get-FileHash -LiteralPath $InstallerArchive).Hash -ne $installerHash)
{
    throw 'Official IPE 6.20 installer identity differs.'
}
if (!(Test-Path -LiteralPath (Join-Path $source 'packs/Microchip/PIC16Fxxx_DFP/1.6.156/edc/PIC16F877A.PIC')))
{
    throw 'IPE 6.20 exact PIC16F877A programming data missing.'
}
$target = Join-Path $runtime 'toolsets/microchip.ipe/6.20.0'
if (Test-Path -LiteralPath $target)
{
    throw 'Never overwrite a prepared component.'
}
$stage = Join-Path $runtime ('pic-ipe-prepare-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try
{
    $payload = Join-Path $stage 'payload'
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    $directories = @('mplab_platform/mplab_ipe', 'mplab_platform/mplablibs', 'mplab_platform/mdbcore',
        'mplab_platform/platform', 'mplab_platform/ide', 'sys/java/zulu8.64.0.19-ca-fx-jre8.0.345-win_x64',
        'packs/Microchip/PIC16Fxxx_DFP/1.6.156', 'docs', 'ThirdParty')
    foreach ($relative in $directories)
    {
        $inputDirectory = Join-Path $source $relative
        foreach ($item in Get-ChildItem -LiteralPath $inputDirectory -Recurse -Force)
        {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
            {
                throw 'Official component cannot contain links.'
            }
        }
        $outputDirectory = Join-Path $payload $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $outputDirectory) -Force | Out-Null
        Copy-Item -LiteralPath $inputDirectory -Destination $outputDirectory -Recurse
    }
    # Windows 启动器的 jdkloc 含开发机绝对路径；只调用显式的私有 Java，不保留该路径。
    Remove-Item -LiteralPath (Join-Path $payload 'mplab_platform/mplab_ipe/jdkloc') -Force
    [ordered]@{version  ='6.20';
        source          ='https://ww1.microchip.com/downloads/aemDocuments/documents/DEV/ProductDocuments/SoftwareTools/MPLABX-v6.20-windows-installer.exe';
        installerSha256 =$installerHash;
        scope           ='Private PICkit3 runtime for PIC16F877A; no global Java or MPLAB installation required';
        programmingDfp  ='PIC16Fxxx_DFP 1.6.156';
        compilerDfp     ='Separately locked by XC8 component';
        licenses        ='docs and ThirdParty; no redistribution grant implied'
    } |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $payload 'sources.json')
    $hashes = [ordered]@{}
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName)
    {
        $hashes[[IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\', '/')] = (Get-FileHash -LiteralPath $file.FullName).Hash
    }
    [ordered]@{formatVersion =1;
        id                   ='microchip.ipe';
        version              ='6.20.0';
        host                 ='win-x64';
        compilerId           ='ipecmd-6.20';
        displayName          ='PICkit3 standalone runtime / IPE 6.20';
        purpose              ='pic-ipe';
        executables          =[ordered]@{java ='sys/java/zulu8.64.0.19-ca-fx-jre8.0.345-win_x64/bin/java.exe';
            ipecmd                            ='mplab_platform/mplab_ipe/ipecmd.jar'
        };
        componentVersions    =[ordered]@{ipe ='6.20';
            PIC16Fxxx_DFP                    ='1.6.156';
            jre                              ='8.0.345'
        };
        resourceDirectories  =[ordered]@{ipe = 'mplab_platform/mplab_ipe' };
        sha256               =$hashes
    } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'toolset.json')
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    [IO.Directory]::Move($payload, $target)
}
finally
{
    $cleanup = [IO.Path]::GetFullPath($stage)
    if (!$cleanup.StartsWith($runtime.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($cleanup) -notmatch '^pic-ipe-prepare-[a-f0-9]{32}$')
    {
        throw 'Private staging cleanup boundary invalid.'
    }
    Remove-Item -LiteralPath $cleanup -Force -Recurse
}
Get-FileHash -LiteralPath (Join-Path $target 'toolset.json')
