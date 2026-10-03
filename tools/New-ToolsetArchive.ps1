param([Parameter(Mandatory)][string]$ToolsetDirectory, [Parameter(Mandatory)][string]$OutputFile,
    [string]$SevenZipPath)
$ErrorActionPreference = 'Stop'
$taskSource = [IO.Path]::GetFullPath($ToolsetDirectory)
$taskOutput = [IO.Path]::GetFullPath($OutputFile)
if ([IO.Path]::GetExtension($taskOutput).ToLowerInvariant() -notin @('.mcutoolchain', '.studioxtools')) { throw 'Use .mcutoolchain for development components; existing .studioxtools archives remain supported.' }
if (Test-Path -LiteralPath $taskOutput) { throw 'Archive destination already exists.' }
if ($taskOutput.StartsWith($taskSource.TrimEnd('\')+'\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Archive must be outside its source tree.' }
if (([IO.File]::GetAttributes($taskSource) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Tool archives do not accept a linked source root.' }
$taskManifest = Get-Content -LiteralPath (Join-Path $taskSource 'toolset.json') -Raw | ConvertFrom-Json
if ($taskManifest.formatVersion -ne 1 -or $taskManifest.host -ne 'win-x64') { throw 'Unsupported toolset manifest.' }
if ($taskManifest.id -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$' -or $taskManifest.version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or !$taskManifest.compilerId -or !$taskManifest.executables -or !$taskManifest.sha256) { throw 'Development component identity or file index is incomplete.' }
$taskFiles = @(Get-ChildItem -LiteralPath $taskSource -Recurse -Force)
if (@($taskFiles | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count) { throw 'Tool archives do not accept linked files/directories.' }
$taskPayload = @($taskFiles | Where-Object { !$_.PSIsContainer -and $_.FullName -ne (Join-Path $taskSource 'toolset.json') })
if ($taskPayload.Count -ne @($taskManifest.sha256.PSObject.Properties).Count) { throw 'Manifest file set differs from the source tree.' }
$taskManifestFile = Get-Item -LiteralPath (Join-Path $taskSource 'toolset.json')
$taskUnpackedBytes = [long](($taskPayload | Measure-Object Length -Sum).Sum) + $taskManifestFile.Length
if ($taskPayload.Count -lt 1 -or $taskPayload.Count + 1 -gt 200000 -or $taskUnpackedBytes -gt 40GB -or $taskManifestFile.Length -gt 32MB) { throw 'Component exceeds IDE file-count, unpacked-size or manifest limits.' }
foreach ($taskFile in $taskPayload) {
    $taskRelative = [IO.Path]::GetRelativePath($taskSource,$taskFile.FullName).Replace('\','/')
    if ($taskRelative -match '^supra/(.*\/)?license(\.txt|/|$)') { throw 'Private Supra licenses cannot enter distributable archives.' }
    $taskExpected = $taskManifest.sha256.PSObject.Properties[$taskRelative].Value
    if (!$taskExpected -or (Get-FileHash -LiteralPath $taskFile.FullName -Algorithm SHA256).Hash -ne $taskExpected) { throw "Toolset hash mismatch: $taskRelative" }
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($taskOutput)) | Out-Null
# 发布制作使用原生 7-Zip 提高大 SDK 压缩速度；IDE 自带托管读写器，用户无需安装此工具。
if (!$SevenZipPath) {
    $taskCommand = Get-Command '7z.exe' -CommandType Application -ErrorAction SilentlyContinue
    $taskSystemSevenZip = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) '7-Zip/7z.exe'
    if ($taskCommand) { $SevenZipPath = $taskCommand.Source }
    elseif (Test-Path -LiteralPath $taskSystemSevenZip) { $SevenZipPath = $taskSystemSevenZip }
    else { throw 'Release packing requires 7z.exe. Pass -SevenZipPath; this does not change system PATH.' }
}
$SevenZipPath = [IO.Path]::GetFullPath($SevenZipPath)
if (!(Test-Path -LiteralPath $SevenZipPath -PathType Leaf)) { throw '7z.exe was not found.' }
$taskTemporary = $taskOutput + '.tmp-' + [Guid]::NewGuid().ToString('N')
$taskList = $taskTemporary + '.files.txt'
$taskLog = $taskOutput + '.packing.log'
$taskPrevious = Get-Location
try {
    # 显式文件列表不加入目录；清单单独一个块，预览无需解压其它文件。
    $taskNames = @($taskPayload | ForEach-Object { [IO.Path]::GetRelativePath($taskSource,$_.FullName).Replace('\','/') } | Sort-Object -CaseSensitive)
    [IO.File]::WriteAllLines($taskList, $taskNames, [Text.UTF8Encoding]::new($false))
    Set-Location -LiteralPath $taskSource
    & $SevenZipPath a '-t7z' '-m0=LZMA2' '-mx=5' '-md=32m' '-ms=off' '-mmt=2' '-bd' '-y' '-spd' '--' $taskTemporary 'toolset.json' *> $taskLog
    if ($LASTEXITCODE -ne 0) { throw "7z manifest packing failed; see $taskLog" }
    & $SevenZipPath a '-t7z' '-m0=LZMA2' '-mx=5' '-md=32m' '-ms=64m' '-mmt=2' '-bd' '-y' '-spd' '-scsUTF-8' $taskTemporary "@$taskList" *>> $taskLog
    if ($LASTEXITCODE -ne 0) { throw "7z payload packing failed; see $taskLog" }
    & $SevenZipPath t '-t7z' '-bd' '--' $taskTemporary *>> $taskLog
    if ($LASTEXITCODE -ne 0) { throw "7z archive integrity test failed; see $taskLog" }
    [IO.File]::Move($taskTemporary, $taskOutput, $false)
} finally {
    Set-Location -LiteralPath $taskPrevious.Path
    if (Test-Path -LiteralPath $taskList) { Remove-Item -LiteralPath $taskList }
    if (Test-Path -LiteralPath $taskTemporary) { Remove-Item -LiteralPath $taskTemporary }
}
Get-FileHash -LiteralPath $taskOutput -Algorithm SHA256
