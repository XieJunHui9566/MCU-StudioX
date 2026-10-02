param([Parameter(Mandatory)][string]$SourceDirectory, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($SourceDirectory))
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput) { throw 'Catalog publication requires a new output directory.' }
$taskCatalogPath = Join-Path $taskSource 'catalog.json'
if ((Get-Item -LiteralPath $taskCatalogPath).Length -gt 1MB) { throw 'Catalog exceeds 1 MiB.' }
$taskCatalog = Get-Content -LiteralPath $taskCatalogPath -Raw | ConvertFrom-Json
if ($taskCatalog.formatVersion -ne 1 -or !$taskCatalog.publisher -or $taskCatalog.entries.Count -gt 1000) { throw 'Invalid catalog.' }
$taskFiles = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
$taskFiles.Add('catalog.json', $taskCatalogPath)
foreach ($taskEntry in $taskCatalog.entries) {
    $taskRelative = [string]$taskEntry.archive
    if (!$taskRelative -or $taskRelative -match '(^|[\\/])\.\.([\\/]|$)|:|^[\\/]') { throw 'Bundled catalogs must reference local relative archives.' }
    $taskArchive = [IO.Path]::GetFullPath((Join-Path $taskSource $taskRelative))
    if (!$taskArchive.StartsWith($taskSource + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive leaves the catalog directory.' }
    if ((Get-Item -LiteralPath $taskArchive).Length -ne $taskEntry.downloadBytes -or (Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash -ne $taskEntry.sha256) { throw "Archive differs from catalog: $taskRelative" }
    $taskFiles[$taskRelative] = $taskArchive
}
foreach ($taskSuffix in @('.sig','.pub.pem')) {
    if (Test-Path -LiteralPath ($taskCatalogPath + $taskSuffix)) { $taskFiles.Add('catalog.json' + $taskSuffix, $taskCatalogPath + $taskSuffix) }
}
# 只复制目录、声明的归档、签名与公钥；构建输出、元数据及私钥不进入发行载荷。
foreach ($taskFile in $taskFiles.Values) {
    $taskAncestor = Get-Item -LiteralPath $taskFile
    while ($taskAncestor) {
        if (($taskAncestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked catalog files/directories cannot be published.' }
        $taskAncestor = if ($taskAncestor.PSIsContainer) { $taskAncestor.Parent } else { $taskAncestor.Directory }
    }
}
foreach ($taskPair in $taskFiles.GetEnumerator()) {
    $taskTarget = Join-Path $taskOutput $taskPair.Key
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskTarget)) | Out-Null
    Copy-Item -LiteralPath $taskPair.Value -Destination $taskTarget
}
Write-Output $taskOutput
