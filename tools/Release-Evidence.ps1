function Get-StudioXSourceEvidence([string]$SourceDirectory)
{
    $commit = (& git -C $SourceDirectory rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Source commit unavailable.'
    }
    $dirty = @(& git -C $SourceDirectory status --porcelain=v1)
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Source status unavailable.'
    }
    return @{sourceCommit =$commit;
        sourceDirty       =($dirty.Count -gt 0)
    }
}
function Write-StudioXReleaseChecksums([string[]]$Files, [string]$OutputFile)
{
    $lines = @($Files | ForEach-Object {
            $file = Get-Item -LiteralPath $_
            if ($file.Name.IndexOfAny([char[]]"`r`n") -ge 0 -or $file.PSIsContainer)
            {
                throw 'Invalid checksum file.'
            }
            (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant() + '  ' + $file.Name
        })
    if ($lines.Count -ne $Files.Count)
    {
        throw 'Checksum entry missing.'
    }
    $lines | Set-Content -LiteralPath $OutputFile -Encoding ascii
}
