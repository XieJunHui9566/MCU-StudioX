param([string]$CatalogRoot)

$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Ag32-PublishAssets.ps1')
if (!$CatalogRoot)
{
    $CatalogRoot = Join-Path $repository 'artifacts/device-packs-development'
}
$sourcePacks = @(Get-Ag32ReleasePacks $CatalogRoot)
$fixture = Join-Path $repository ('artifacts/validation/ag32-publish-assets-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory((Join-Path $fixture 'AGM')) | Out-Null
foreach ($entry in $sourcePacks)
{
    Copy-Item -LiteralPath (Resolve-Ag32PublishPath $CatalogRoot $entry.file) -Destination (Join-Path $fixture $entry.file)
}
$baseline = ConvertTo-Json -InputObject $sourcePacks -Depth 20
$indexPath = Join-Path $fixture 'index.json'

function Reset-Ag32PublishFixtureIndex
{
    [IO.File]::WriteAllText($indexPath, $baseline, [Text.UTF8Encoding]::new($false))
}

function Save-Ag32PublishFixtureIndex($Entries)
{
    [IO.File]::WriteAllText($indexPath, (ConvertTo-Json -InputObject $Entries -Depth 20), [Text.UTF8Encoding]::new($false))
}

function Assert-Ag32PublishRejected([string]$Name, [scriptblock]$Action, [string]$Diagnostic)
{
    try
    {
        $null = & $Action
    }
    catch
    {
        if ($_.Exception.Message -notlike $Diagnostic)
        {
            throw
        }
        Write-Output "PASS $Name"
        return
    }
    throw "Expected AG32 publish validation to reject: $Name"
}

Reset-Ag32PublishFixtureIndex
$actual = @(Get-Ag32ReleasePacks $fixture)
if ($actual.Count -ne 4 -or @($actual | ForEach-Object { $_.devices }).Count -ne 7)
{
    throw 'Valid AG32 fixture did not return four packs and seven devices.'
}
Write-Output 'PASS real four-pack catalog and isolated fixture'

# 只改动四个小包的隔离副本，不改 SDK、当前开发资源和已发布版本。
$last = $sourcePacks[-1]
$lastPath = Join-Path $fixture $last.file
Remove-Item -LiteralPath $lastPath
Assert-Ag32PublishRejected 'missing archive' { Get-Ag32ReleasePacks $fixture } '*AG32 pack missing:*'
Copy-Item -LiteralPath (Resolve-Ag32PublishPath $CatalogRoot $last.file) -Destination $lastPath

$content = [IO.File]::ReadAllBytes($lastPath)
$content[0] = $content[0] -bxor 1
[IO.File]::WriteAllBytes($lastPath, $content)
Assert-Ag32PublishRejected 'tampered archive' { Get-Ag32ReleasePacks $fixture } '*AG32 pack hash mismatch:*'
Copy-Item -LiteralPath (Resolve-Ag32PublishPath $CatalogRoot $last.file) -Destination $lastPath -Force

$entries = @($baseline | ConvertFrom-Json)
$entries[0].sha256 = '0' * 64
Save-Ag32PublishFixtureIndex $entries
Assert-Ag32PublishRejected 'wrong SHA-256 in index' { Get-Ag32ReleasePacks $fixture } '*AG32 pack hash mismatch:*'

foreach ($escape in @('../outside.mcupack', 'AGM/../../outside.mcupack', 'AGM/./pack.mcupack', $lastPath, 'AGM/pack.mcupack:stream'))
{
    $entries = @($baseline | ConvertFrom-Json)
    $entries[0].file = $escape
    Save-Ag32PublishFixtureIndex $entries
    Assert-Ag32PublishRejected ('unsafe path ' + $escape) { Get-Ag32ReleasePacks $fixture } '*Invalid AG32 asset path:*'
}

$link = Join-Path $fixture 'linked-AGM'
$null = New-Item -ItemType Junction -Path $link -Target (Join-Path $fixture 'AGM')
try
{
    Assert-Ag32PublishRejected 'catalog directory junction' {
        Resolve-Ag32PublishPath $fixture ('linked-AGM/' + [IO.Path]::GetFileName($last.file))
    } '*AG32 asset contains a reparse point:*'
}
finally
{
    # 删除夹具中的链接入口，不递归处理它指向的真实包目录。
    Remove-Item -LiteralPath $link
}

$entries = @($baseline | ConvertFrom-Json)
Save-Ag32PublishFixtureIndex @($entries[0..2])
Assert-Ag32PublishRejected 'missing pack in index' { Get-Ag32ReleasePacks $fixture } '*exactly four current device packs*'

$entries = @($baseline | ConvertFrom-Json)
$entries[1] = $entries[0]
Save-Ag32PublishFixtureIndex $entries
Assert-Ag32PublishRejected 'duplicate pack' { Get-Ag32ReleasePacks $fixture } '*AG32 pack index identity mismatch:*'

$entries = @($baseline | ConvertFrom-Json)
$entries[0].devices[1] = $entries[0].devices[0]
Save-Ag32PublishFixtureIndex $entries
Assert-Ag32PublishRejected 'duplicate device' { Get-Ag32ReleasePacks $fixture } '*AG32 index contains a duplicate device:*'

$entries = @($baseline | ConvertFrom-Json)
$entries[0].provenanceSha256 = '0' * 64
Save-Ag32PublishFixtureIndex $entries
Assert-Ag32PublishRejected 'wrong provenance hash' { Get-Ag32ReleasePacks $fixture } '*AG32 pack provenance hash mismatch:*'

$entries = @($baseline | ConvertFrom-Json)
$entries[0].version = '0.1.1'
Save-Ag32PublishFixtureIndex $entries
Assert-Ag32PublishRejected 'stale VF303 version' { Get-Ag32ReleasePacks $fixture } '*AG32 pack index identity mismatch:*'

# 故意构造重新计算外部 SHA 的不一致 ZIP，证明不能仅信任索引中的型号声明。
Reset-Ag32PublishFixtureIndex
$archive = [IO.Compression.ZipFile]::Open($lastPath, [IO.Compression.ZipArchiveMode]::Update)
try
{
    $oldEntry = $archive.GetEntry('manifest.json')
    $reader = [IO.StreamReader]::new($oldEntry.Open())
    try
    {
        $manifest = $reader.ReadToEnd() | ConvertFrom-Json
    }
    finally
    {
        $reader.Dispose()
    }
    $oldEntry.Delete()
    $manifest.version = '9.9.9'
    $replacement = $archive.CreateEntry('manifest.json')
    $writer = [IO.StreamWriter]::new($replacement.Open(), [Text.UTF8Encoding]::new($false))
    try
    {
        $writer.Write((ConvertTo-Json -InputObject $manifest -Depth 20))
    }
    finally
    {
        $writer.Dispose()
    }
}
finally
{
    $archive.Dispose()
}
$entries = @($baseline | ConvertFrom-Json)
$entries[-1].sha256 = (Get-FileHash -LiteralPath $lastPath -Algorithm SHA256).Hash.ToLowerInvariant()
Save-Ag32PublishFixtureIndex $entries
Assert-Ag32PublishRejected 'archive identity contradicts index' { Get-Ag32ReleasePacks $fixture } '*AG32 archive identity disagrees with index:*'
Copy-Item -LiteralPath (Resolve-Ag32PublishPath $CatalogRoot $last.file) -Destination $lastPath -Force
Reset-Ag32PublishFixtureIndex
$null = Get-Ag32ReleasePacks $fixture
Write-Output "AG32 publish assets validation completed. Fixture: $fixture"
