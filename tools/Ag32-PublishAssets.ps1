# AGM 发布资源只读取固定目录中的新格式包，可脱离完整 IDE 发布单独校验。
function Resolve-Ag32PublishPath([string]$Root, [string]$Relative)
{
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or
        ($Relative.Replace('\', '/').Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count)
    {
        throw "Invalid AG32 asset path: $Relative"
    }
    $absoluteRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $prefix = $absoluteRoot + [IO.Path]::DirectorySeparatorChar
    $path = [IO.Path]::GetFullPath((Join-Path $absoluteRoot $Relative))
    if (!$path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "AG32 asset path escapes its catalog: $Relative"
    }
    $current = $path
    while ($current -eq $absoluteRoot -or $current.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase))
    {
        if (([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) -and
            ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint))
        {
            throw "AG32 asset contains a reparse point: $current"
        }
        if ($current -eq $absoluteRoot)
        {
            break
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $path
}

function Get-Ag32ReleasePacks([string]$CatalogRoot)
{
    $expected = @{
        'studiox.preview.ag32vf303' = @{
            version = '0.1.5'
            devices = @('AG32VF303KCU6', 'AG32VF303CCT6', 'AG32VF303VCT6')
        }
        'agm.ag32vf407'             = @{
            version = '0.1.3'
            devices = @('AG32VF407RGT6', 'AG32VF407VGT6')
        }
        'agm.ag32vh303'             = @{
            version = '0.1.3'
            devices = @('AG32VH303RCT6')
        }
        'agm.ag32vh407'             = @{
            version = '0.1.3'
            devices = @('AG32VH407VGT6')
        }
    }
    $indexPath = Resolve-Ag32PublishPath $CatalogRoot 'index.json'
    $index = @(Get-Content -LiteralPath $indexPath -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json)
    # 开发目录的总索引同时包含其它厂商；AGM 候选中的未知、重复和旧包必须拒绝。
    $entries = @($index | Where-Object {
            $_.id -eq 'studiox.preview.ag32vf303' -or $_.id -like 'agm.*' -or $_.file -like 'AGM/*'
        })
    if ($entries.Count -ne 4)
    {
        throw 'AG32 release requires exactly four current device packs.'
    }
    $seenPacks = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $seenDevices = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries)
    {
        # 先校验路径，恶意路径不能成为后续哈希或 ZIP 读取的目标。
        $source = Resolve-Ag32PublishPath $CatalogRoot $entry.file
        if (!$expected.ContainsKey($entry.id) -or !$seenPacks.Add($entry.id))
        {
            throw "AG32 pack index identity mismatch: $($entry.id)"
        }
        $profile = $expected[$entry.id]
        $filename = 'AGM/' + $entry.id + '-' + $profile.version + '.mcupack'
        $deviceIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($deviceId in @($entry.devices))
        {
            if (!$deviceIds.Add($deviceId) -or !$seenDevices.Add($deviceId))
            {
                throw "AG32 index contains a duplicate device: $deviceId"
            }
        }
        if ($entry.version -cne $profile.version -or $entry.file -cne $filename -or
            !$deviceIds.SetEquals([string[]]$profile.devices) -or
            $entry.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
            $entry.provenanceSha256 -cnotmatch '^[0-9a-f]{64}$')
        {
            throw "AG32 pack index identity mismatch: $($entry.id)"
        }
        if (!(Test-Path -LiteralPath $source -PathType Leaf))
        {
            throw "AG32 pack missing: $source"
        }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
        {
            throw "AG32 pack hash mismatch: $source"
        }
        $archive = [IO.Compression.ZipFile]::OpenRead($source)
        try
        {
            $manifestEntry = $archive.GetEntry('manifest.json')
            $provenanceEntry = $archive.GetEntry('vendor/provenance.json')
            if (!$manifestEntry -or !$provenanceEntry)
            {
                throw "AG32 pack evidence missing: $source"
            }
            $stream = $provenanceEntry.Open()
            try
            {
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            }
            finally
            {
                $stream.Dispose()
            }
            if ($hash -cne $entry.provenanceSha256)
            {
                throw "AG32 pack provenance hash mismatch: $source"
            }
            $reader = [IO.StreamReader]::new($manifestEntry.Open())
            try
            {
                $manifest = $reader.ReadToEnd() | ConvertFrom-Json
            }
            finally
            {
                $reader.Dispose()
            }
            $archiveIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($device in @($manifest.devices))
            {
                if (!$archiveIds.Add($device.id))
                {
                    throw "AG32 archive contains a duplicate device: $($device.id)"
                }
            }
            if ($manifest.formatVersion -ne 1 -or $manifest.vendor -cne 'AGM' -or
                $manifest.id -cne $entry.id -or $manifest.version -cne $entry.version -or
                !$archiveIds.SetEquals([string[]]$profile.devices))
            {
                throw "AG32 archive identity disagrees with index: $source"
            }
        }
        finally
        {
            $archive.Dispose()
        }
    }
    if ($seenDevices.Count -ne 7)
    {
        throw 'AG32 release requires seven distinct verified MCU ordering codes.'
    }
    return $entries
}
