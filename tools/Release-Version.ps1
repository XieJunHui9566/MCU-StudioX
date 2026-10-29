# 产品允许三/四段数字及单个大写修订字母；已发行的 LTS 名称使用固定数字映射。
function Get-StudioXReleaseVersion([string]$Value)
{
    if ($Value -ceq '0.2.6LTS')
    {
        # 维护版沿用用户指定名称，Windows 构建号须高于已发布的 0.2.6.10。
        return [pscustomobject]@{ ProductVersion = $Value;
            FileVersion                          = '0.2.6.11';
            Suffix                               = 'LTS'
        }
    }
    if ($Value -cnotmatch '^(?<Numeric>\d+\.\d+\.\d+(?:\.\d+)?)(?<Suffix>[A-Z]?)$')
    {
        throw 'ReleaseVersion must contain three or four numeric parts and an optional uppercase A..Z revision, or the registered 0.2.6LTS identity.'
    }
    $numeric = $Matches.Numeric
    $suffix = $Matches.Suffix
    if (($numeric.Split('.') | Where-Object { $_.Length -gt 5 -or [int]$_ -gt 65535 }).Count)
    {
        throw 'ReleaseVersion numeric parts must be in the range 0..65535.'
    }
    $fileVersion = if ($numeric.Split('.').Count -eq 3)
    {
        "$numeric.0"
    }
    else
    {
        $numeric
    }
    return [pscustomobject]@{ ProductVersion = $Value;
        FileVersion                          = $fileVersion;
        Suffix                               = $suffix
    }
}

function Compare-StudioXReleaseVersions([string]$Left, [string]$Right)
{
    $a = Get-StudioXReleaseVersion $Left
    $b = Get-StudioXReleaseVersion $Right
    $comparison = ([version]$a.FileVersion).CompareTo([version]$b.FileVersion)
    if ($comparison -ne 0)
    {
        return $comparison
    }
    return [string]::CompareOrdinal($a.Suffix, $b.Suffix)
}

function Assert-StudioXSourceReleaseIdentity([string]$SourceDirectory, [string]$DesktopAssembly)
{
    [xml]$props = Get-Content -LiteralPath (Join-Path $SourceDirectory 'Directory.Build.props') -Raw
    $version = $props.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
    $product = $props.SelectSingleNode('/Project/PropertyGroup/ProductVersion').InnerText
    $identity = Get-StudioXReleaseVersion $product
    if ($version -cne $product)
    {
        throw 'Source Version and ProductVersion differ.'
    }
    $readme = Get-Content -LiteralPath (Join-Path $SourceDirectory 'README.md') -Raw
    if ($readme -cnotmatch ('当前源码版本 \*\*' + [regex]::Escape($product) + '\*\*'))
    {
        throw 'README current source version differs from ProductVersion.'
    }
    if ($DesktopAssembly)
    {
        $file = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath($DesktopAssembly))
        if ($file.FileVersion -cne $identity.FileVersion -or ($file.ProductVersion -split '\+')[0] -cne $product)
        {
            throw 'Desktop file/product version differs from the source release identity.'
        }
    }
    return $identity
}
