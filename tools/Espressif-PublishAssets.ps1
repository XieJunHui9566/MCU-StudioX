# 发行前核对共享 SDK 与小型器件包，不下载工具或启动硬件。
function Resolve-EspressifPublishPath([string]$Root, [string]$Relative)
{
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or ($Relative.Replace('\', '/').Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count)
    {
        throw "Invalid Espressif asset path: $Relative"
    }
    $absoluteRoot = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $path = [IO.Path]::GetFullPath((Join-Path $absoluteRoot $Relative))
    if (!$path.StartsWith($absoluteRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
    {
        throw "Espressif asset path escapes its root: $Relative"
    }
    $current = $path
    while ($current -and $current.StartsWith($absoluteRoot, [StringComparison]::OrdinalIgnoreCase))
    {
        if (([IO.File]::Exists($current) -or [IO.Directory]::Exists($current)) -and
            ([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint))
        {
            throw "Espressif asset contains a reparse point: $current"
        }
        if ($current -eq $absoluteRoot)
        {
            break
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $path
}

function Get-EspressifReleasePacks([string]$CatalogRoot)
{
    $expected = @{
        'espressif.esp32-wroom-32' = @('ESP32-WROOM-32', 'esp32')
        'espressif.esp32p4'        = @('ESP32-P4', 'esp32p4')
        'espressif.esp32s3'        = @('ESP32-S3', 'esp32s3')
        'espressif.esp32c3'        = @('ESP32-C3', 'esp32c3')
        'espressif.esp32c5'        = @('ESP32-C5', 'esp32c5')
        'espressif.esp32c6'        = @('ESP32-C6', 'esp32c6')
        'espressif.esp8266'        = @('ESP8266', 'esp8266')
    }
    $indexPath = Resolve-EspressifPublishPath $CatalogRoot 'index.json'
    $index = @(Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json)
    if ($index.Count -ne $expected.Count)
    {
        throw 'Espressif release requires exactly seven device packs.'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $index)
    {
        $source = Resolve-EspressifPublishPath $CatalogRoot $entry.file
        $is8266 = $entry.id -eq 'espressif.esp8266'
        $packVersion = if ($is8266)
        {
            '0.1.0'
        }
        else
        {
            '0.1.1'
        }
        $framework = if ($is8266)
        {
            'esp8266-rtos-sdk'
        }
        else
        {
            'esp-idf'
        }
        $sdkVersion = if ($is8266)
        {
            '3.4.0'
        }
        else
        {
            '5.5.4'
        }
        if (!$expected.ContainsKey($entry.id) -or !$seen.Add($entry.id) -or $entry.version -ne $packVersion -or
            $entry.file -ne ($entry.id + '-' + $packVersion + '.mcupack') -or $entry.framework -ne $framework -or
            $entry.sdkVersion -ne $sdkVersion -or $entry.target -ne $expected[$entry.id][1] -or
            @($entry.devices).Count -ne 1 -or $entry.devices[0] -ne $expected[$entry.id][0] -or
            $entry.sha256 -notmatch '^[0-9a-f]{64}$' -or $entry.provenanceSha256 -notmatch '^[0-9a-f]{64}$')
        {
            throw "Espressif pack index identity mismatch: $($entry.id)"
        }
        if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256)
        {
            throw "Espressif pack hash mismatch: $source"
        }
        $archive = [IO.Compression.ZipFile]::OpenRead($source)
        try
        {
            $provenance = $archive.GetEntry('provenance.json')
            $manifestEntry = $archive.GetEntry('manifest.json')
            if (!$provenance -or !$manifestEntry)
            {
                throw "Espressif pack evidence missing: $source"
            }
            $stream = $provenance.Open()
            try
            {
                $provenanceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
            }
            finally
            {
                $stream.Dispose()
            }
            if ($provenanceHash -ne $entry.provenanceSha256)
            {
                throw "Espressif pack provenance hash mismatch: $source"
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
            $device = @($manifest.devices)[0]
            if ($manifest.formatVersion -ne 1 -or $manifest.id -ne $entry.id -or $manifest.version -ne $entry.version -or
                @($manifest.devices).Count -ne 1 -or $device.id -ne $entry.devices[0] -or
                $device.espressif.framework -ne $framework -or $device.espressif.sdkVersion -ne $sdkVersion -or $device.espressif.target -ne $entry.target)
            {
                throw "Espressif archive identity disagrees with index: $source"
            }
        }
        finally
        {
            $archive.Dispose()
        }
    }
    return $index
}

function Test-EspressifPublishToolsets([string]$ToolsetsRoot, [string]$ToolsetId = '')
{
    if ($ToolsetId -and $ToolsetId -notin @('espressif.idf', 'espressif.esp8266-rtos'))
    {
        throw "Unknown Espressif toolset verification filter: $ToolsetId"
    }
    foreach ($profile in @(@('espressif.idf', '5.5.4', 'esp-idf'), @('espressif.esp8266-rtos', '3.4.0', 'esp8266-rtos-sdk')))
    {
        if ($ToolsetId -and $profile[0] -ne $ToolsetId)
        {
            continue
        }
        $root = Resolve-EspressifPublishPath $ToolsetsRoot ($profile[0] + '/' + $profile[1])
        $manifestPath = Resolve-EspressifPublishPath $root 'toolset.json'
        $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
        $compilerId = if ($profile[2] -eq 'esp-idf')
        {
            'esp-idf'
        }
        else
        {
            'esp8266-rtos'
        }
        if ($manifest.formatVersion -ne 1 -or $manifest.id -ne $profile[0] -or $manifest.version -ne $profile[1] -or
            $manifest.host -ne 'win-x64' -or $manifest.compilerId -ne $compilerId -or $manifest.purpose -ne $profile[2] -or
            $manifest.componentVersions[$profile[2]] -ne $profile[1] -or !$manifest.sha256 -or !$manifest.executables)
        {
            throw "Espressif toolset identity mismatch: $root"
        }
        foreach ($role in @('idf', 'tools', 'python-env'))
        {
            if (!$manifest.resourceDirectories[$role] -or !(Test-Path -LiteralPath (Resolve-EspressifPublishPath $root $manifest.resourceDirectories[$role]) -PathType Container))
            {
                throw "Espressif resource missing: $($profile[0]) / $role"
            }
            $resource = Resolve-EspressifPublishPath $root $manifest.resourceDirectories[$role]
            if (!@([IO.Directory]::EnumerateFileSystemEntries($resource)).Count)
            {
                throw "Espressif resource directory would disappear from file-based publish: $resource"
            }
        }
        if ($profile[2] -eq 'esp8266-rtos-sdk' -and !$manifest.sha256.Contains('idf-tools/README.txt'))
        {
            throw 'ESP8266 tool-state resource must contain its indexed README.'
        }
        $targets = if ($profile[2] -eq 'esp-idf')
        {
            @('esp32', 'esp32s3', 'riscv')
        }
        else
        {
            @('esp8266')
        }
        $roles = @('python', 'cmake', 'ninja', 'git') + @($targets | ForEach-Object { "gcc-$_"; "gxx-$_"; "size-$_" })
        foreach ($role in $roles)
        {
            $relative = $manifest.executables[$role]
            if (!$relative -or !$manifest.sha256.Contains($relative))
            {
                throw "Unindexed Espressif executable: $role"
            }
        }
        foreach ($relative in @('SOURCE.json', 'sdk/version.txt', 'sdk/LICENSE', 'sdk/tools/idf.py', 'sdk/tools/cmake/project.cmake'))
        {
            if (!$manifest.sha256.Contains($relative))
            {
                throw "Unindexed Espressif SDK evidence: $relative"
            }
        }
        # 发布是独立校验入口，不能沿用 IDE 进程中的元数据快照。
        $actualFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($item in Get-ChildItem -LiteralPath $root -Force -Recurse)
        {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
            {
                throw "Espressif toolset link is unsupported: $($item.FullName)"
            }
            if (!$item.PSIsContainer)
            {
                $null = $actualFiles.Add([IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/'))
            }
        }
        $null = $actualFiles.Remove('toolset.json')
        if (!$actualFiles.SetEquals([string[]]$manifest.sha256.Keys))
        {
            throw "Espressif toolset file inventory mismatch: $root"
        }
        foreach ($relative in $manifest.sha256.Keys)
        {
            $path = Resolve-EspressifPublishPath $root $relative
            $digest = $manifest.sha256[$relative]
            if ($digest -notmatch '^[0-9a-f]{64}$' -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $digest)
            {
                throw "Espressif toolset file hash mismatch: $path"
            }
        }
        $sdkVersion = Get-Content -LiteralPath (Resolve-EspressifPublishPath $root 'sdk/version.txt') -Raw
        $expectedVersion = if ($profile[2] -eq 'esp-idf')
        {
            'v5.5.4'
        }
        else
        {
            'v3.4'
        }
        if ($sdkVersion.Trim() -ne $expectedVersion)
        {
            throw "Espressif SDK version file mismatch: $root"
        }
        Write-Host "Verified Espressif publish assets: $($profile[0]) $($profile[1]), $($actualFiles.Count) files"
    }
}
