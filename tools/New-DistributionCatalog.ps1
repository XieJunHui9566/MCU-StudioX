param([Parameter(Mandatory)][string]$MetadataFile, [Parameter(Mandatory)][string]$OutputFile, [string]$PrivateKeyFile)
$ErrorActionPreference = 'Stop'
$taskMetadata = [IO.Path]::GetFullPath($MetadataFile)
$taskOutput = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $taskOutput) { throw 'Catalog destination already exists.' }
$taskInput = Get-Content -LiteralPath $taskMetadata -Raw | ConvertFrom-Json
if (!$taskInput.publisher -or !$taskInput.entries.Count) { throw 'Metadata needs publisher and entries.' }
$taskCatalog = [ordered]@{formatVersion=1;publisher=$taskInput.publisher;entries=@()}
foreach ($taskEntry in $taskInput.entries) {
    if ($taskEntry.archive -match '(^|[\\/])\.\.([\\/]|$)|:|^[\\/]') { throw 'Archive paths must be relative to the metadata directory.' }
    $taskArchive = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($taskMetadata)) $taskEntry.archive))
    if ([IO.Path]::GetDirectoryName($taskOutput) -ne [IO.Path]::GetDirectoryName($taskMetadata)) { throw 'Write catalog beside metadata and its relative archives.' }
    if (!$taskEntry.license -or !$taskEntry.sourceUrl -or !$taskEntry.releaseNotes) { throw 'Specify license, public source URL and release notes for each entry.' }
    $taskManifestFile = switch ($taskEntry.kind) { 'tool' {'toolset.json'} 'plugin' {'plugin.json'} 'component' {'component.json'} default { throw 'Unsupported kind.' } }
    $taskZip = [IO.Compression.ZipFile]::OpenRead($taskArchive)
    try {
        $taskManifestEntry = $taskZip.GetEntry($taskManifestFile)
        if (!$taskManifestEntry -or $taskManifestEntry.Length -gt 1MB) { throw 'Archive manifest missing or too large.' }
        $taskReader = [IO.StreamReader]::new($taskManifestEntry.Open())
        try { $taskManifest = $taskReader.ReadToEnd() | ConvertFrom-Json } finally { $taskReader.Dispose() }
        $taskBytes = ($taskZip.Entries | Measure-Object -Property Length -Sum).Sum
    } finally { $taskZip.Dispose() }
    $taskCatalog.entries += [ordered]@{kind=$taskEntry.kind;id=$taskManifest.id;version=$taskManifest.version;name=$(if ($taskManifest.displayName) {$taskManifest.displayName} elseif ($taskManifest.name) {$taskManifest.name} else {$taskManifest.id});archive=$taskEntry.archive.Replace('\','/');sha256=(Get-FileHash -LiteralPath $taskArchive -Algorithm SHA256).Hash;downloadBytes=(Get-Item -LiteralPath $taskArchive).Length;installedBytes=[long]$taskBytes;license=$taskEntry.license;sourceUrl=$taskEntry.sourceUrl;releaseNotes=$taskEntry.releaseNotes;pluginApi=$(if ($taskEntry.kind -eq 'plugin') {$taskManifest.apiVersion} else {0});frameworks=@($taskManifest.frameworks)}
}
$taskJson = $taskCatalog | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($taskOutput,$taskJson,[Text.UTF8Encoding]::new($false))
if ($PrivateKeyFile) {
    $taskRsa = [Security.Cryptography.RSA]::Create()
    try {
        $taskRsa.ImportFromPem([IO.File]::ReadAllText([IO.Path]::GetFullPath($PrivateKeyFile)))
        $taskSignature = $taskRsa.SignData([IO.File]::ReadAllBytes($taskOutput),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)
        [IO.File]::WriteAllText($taskOutput+'.sig',[Convert]::ToBase64String($taskSignature))
        [IO.File]::WriteAllText($taskOutput+'.pub.pem',$taskRsa.ExportSubjectPublicKeyInfoPem())
    } finally { $taskRsa.Dispose() }
}
Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256
