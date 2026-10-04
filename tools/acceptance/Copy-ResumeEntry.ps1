function Copy-ResumeEntry([string]$Source, [string]$Target, [string]$TargetRoot, [long]$Bytes, [string]$Sha256)
{
    $boundary = [IO.Path]::GetFullPath($TargetRoot).TrimEnd('\')
    $destination = [IO.Path]::GetFullPath($Target)
    if (!$destination.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Resume copy escaped its selected directory.'
    }
    if ($Sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $Bytes -lt 0)
    {
        throw 'Invalid resume fingerprint.'
    }
    $cursor = $destination
    while ($cursor -and $cursor.Length -ge $boundary.Length)
    {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint))
        {
            throw 'Resume copy refuses links.'
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    if ((Get-Item -LiteralPath $Source).Length -ne $Bytes -or (Get-FileHash -LiteralPath $Source -Algorithm SHA256).Hash -ne $Sha256)
    {
        throw 'Resume source fingerprint differs.'
    }
    $existing = Test-Path -LiteralPath $destination -PathType Leaf
    if ($existing -and (Get-Item -LiteralPath $destination).Length -eq $Bytes -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $Sha256)
    {
        return 'unchanged'
    }
    $directory = [IO.Path]::GetDirectoryName($destination)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $staged = Join-Path $directory ('.' + [IO.Path]::GetFileName($destination) + '.resume-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $attributes = $null
    $published = $false
    try
    {
        [IO.File]::Copy($Source, $staged, $false)
        [IO.File]::SetAttributes($staged, ([IO.File]::GetAttributes($staged) -band (-bnot [IO.FileAttributes]::ReadOnly)))
        if ((Get-Item -LiteralPath $staged).Length -ne $Bytes -or (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash -ne $Sha256)
        {
            throw 'Staged resume copy differs.'
        }
        if ($existing)
        {
            $attributes = [IO.File]::GetAttributes($destination)
            # 仅修改已经校验并限定在私有验收目录内的副本，原始只读共享载荷保持不变。
            [IO.File]::SetAttributes($destination, ($attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
            [IO.File]::Replace($staged, $destination, [NullString]::Value)
        }
        else
        {
            [IO.File]::Move($staged, $destination)
        }
        $published = $true
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -ne $Sha256)
        {
            throw 'Published resume copy differs.'
        }
        return 'updated'
    }
    finally
    {
        if (!$published -and $attributes -ne $null -and (Test-Path -LiteralPath $destination))
        {
            [IO.File]::SetAttributes($destination, $attributes)
        }
        if (Test-Path -LiteralPath $staged)
        {
            [IO.File]::Delete($staged)
        }
    }
}
