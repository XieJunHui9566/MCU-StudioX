# 产品允许三段版本或四段修订版本；Windows 文件版本始终使用四段数字。
function Get-StudioXReleaseVersion([string]$Value)
{
    if ($Value -notmatch '^\d+\.\d+\.\d+(?:\.\d+)?$' -or
        ($Value.Split('.') | Where-Object { $_.Length -gt 5 -or [int]$_ -gt 65535 }).Count)
    {
        throw 'ReleaseVersion must contain three or four numeric parts in the range 0..65535.'
    }
    $fileVersion = if ($Value.Split('.').Count -eq 3) { "$Value.0" } else { $Value }
    return [pscustomobject]@{ ProductVersion = $Value; FileVersion = $fileVersion }
}
