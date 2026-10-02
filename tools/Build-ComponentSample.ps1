param([Parameter(Mandatory)][string]$OutputFile)
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../examples/components/studiox.byte-utils'))
$taskOutput = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $taskOutput) { throw 'Component output already exists.' }
$taskHashes = [ordered]@{}
foreach ($taskFile in Get-ChildItem -LiteralPath $taskSource -Recurse -File) {
    $taskHashes[[IO.Path]::GetRelativePath($taskSource,$taskFile.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash
}
$taskManifest = @{formatVersion=1;id='studiox.byte-utils';version='1.0.0';name='字节读取辅助';description='跨 MCU 的纯 C 头文件示例，不依赖外设';license='MIT';sourceUrl='https://github.com/XieJunHui9566/MCU-StudioX';frameworks=@('cmake','esp-idf');sources=@();includeDirectories=@('include');sha256=$taskHashes}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
$taskZip = [IO.Compression.ZipFile]::Open($taskOutput,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($taskFile in Get-ChildItem -LiteralPath $taskSource -Recurse -File) {
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($taskZip,$taskFile.FullName,[IO.Path]::GetRelativePath($taskSource,$taskFile.FullName).Replace('\','/')) | Out-Null
    }
    $taskDescription = $taskZip.CreateEntry('component.json')
    $taskWriter = [IO.StreamWriter]::new($taskDescription.Open(),[Text.UTF8Encoding]::new($false))
    try { $taskWriter.Write(($taskManifest | ConvertTo-Json -Depth 8)) } finally { $taskWriter.Dispose() }
} finally { $taskZip.Dispose() }
Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256
