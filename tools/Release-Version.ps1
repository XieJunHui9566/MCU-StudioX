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
