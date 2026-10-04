param([switch]$Check, [string]$BuildArtifactsDirectory)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$whitespaceScratch = $null
if ($BuildArtifactsDirectory)
{
    # 与隔离构建共用资产路径；仅影响本脚本的子进程，不改变用户全局配置。
    $previousArtifactsPath = $env:ArtifactsPath
    $previousArtifactsOutput = $env:UseArtifactsOutput
    $env:ArtifactsPath = [IO.Path]::GetFullPath($BuildArtifactsDirectory)
    $env:UseArtifactsOutput = 'true'
}
try
{
    # 公开 DTO 分文件后，仅基于完整工程语义删除无用 using，不猜测引用是否需要。
    $importArguments = @('format', 'style', (Join-Path $projectRoot 'StudioX.slnx'), '--no-restore', '--diagnostics', 'IDE0005', '--severity', 'info')
    if ($Check)
    {
        $importArguments += '--verify-no-changes'
    }
    & dotnet @importArguments
    if ($LASTEXITCODE -ne 0)
    {
        throw 'C# import style check failed.'
    }
    $mode = if ($Check)
    {
        '--check'
    }
    else
    {
        '--write'
    }

    # 结构检查复用 SDK 的 Roslyn；只遍历第一方源码，排除运行时、产物与厂商目录。
    & dotnet run --project (Join-Path $PSScriptRoot 'StudioX.SourceStyle') -- --self-test
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Source formatter boundary checks failed.'
    }
    & dotnet run --no-build --project (Join-Path $PSScriptRoot 'StudioX.SourceStyle') -- $mode $projectRoot
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Structural source style check failed.'
    }

    # folder 模式不还原包；文件逐个列入，避免目录 include 在 SDK 中只匹配目录本身。
    $absoluteFiles = @(& rg --files (Join-Path $projectRoot 'src') (Join-Path $projectRoot 'tools') (Join-Path $projectRoot 'examples') -g '*.cs' -g '!**/bin/**' -g '!**/obj/**' -g '!**/vendor/**' -g '!**/third-party/**' -g '!**/runtime/**' -g '!**/artifacts/**')
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Cannot enumerate first-party C# files.'
    }
    # SDK 的 LF 规则会改写原始字符串的 CRLF 值；先排版隔离副本，再校验并恢复字面量。
    # -Check 也只修改临时副本，避免误报字面量换行或改动产品源码。
    $whitespaceScratch = Join-Path ([IO.Path]::GetTempPath()) ('StudioX-source-style-' + [Guid]::NewGuid().ToString('N'))
    $baselineRoot = Join-Path $whitespaceScratch 'before'
    $formattedRoot = Join-Path $whitespaceScratch 'after'
    [IO.Directory]::CreateDirectory($formattedRoot) | Out-Null
    Copy-Item -LiteralPath (Join-Path $projectRoot '.editorconfig') -Destination (Join-Path $formattedRoot '.editorconfig')
    foreach ($file in $absoluteFiles)
    {
        if (([IO.File]::GetAttributes($file) -band [IO.FileAttributes]::ReparsePoint) -ne 0)
        {
            throw 'First-party source files cannot be links.'
        }
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file)
        foreach ($copyRoot in @($baselineRoot, $formattedRoot))
        {
            $copy = Join-Path $copyRoot $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($copy)) | Out-Null
            Copy-Item -LiteralPath $file -Destination $copy
        }
    }
    # Windows 命令行有长度限制；副本内分批处理，工作区不包含 SDK 或验收产物。
    Push-Location -LiteralPath $projectRoot
    try
    {
        foreach ($sourceName in @('src', 'tools', 'examples'))
        {
            $originalSourceRoot = Join-Path $projectRoot $sourceName
            $sourceRoot = Join-Path $formattedRoot $sourceName
            $files = @($absoluteFiles | Where-Object { $_.StartsWith($originalSourceRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } |
                    ForEach-Object { [IO.Path]::GetRelativePath($originalSourceRoot, $_) })
            for ($offset = 0; $offset -lt $files.Count; $offset += 128)
            {
                $last = [Math]::Min($offset + 127, $files.Count - 1)
                $batch = $files[$offset..$last]
                $formatArguments = @('format', 'whitespace', $sourceRoot, '--folder', '--include') + $batch
                & dotnet @formatArguments
                if ($LASTEXITCODE -ne 0)
                {
                    throw 'C# whitespace style check failed.'
                }
            }
        }
        & dotnet run --no-build --project (Join-Path $PSScriptRoot 'StudioX.SourceStyle') -- --finish-whitespace $projectRoot $baselineRoot $formattedRoot $mode
        if ($LASTEXITCODE -ne 0)
        {
            throw 'C# whitespace token/style verification failed.'
        }
    }
    finally
    {
        Pop-Location
    }
}
finally
{
    try
    {
        if ($whitespaceScratch -and (Test-Path -LiteralPath $whitespaceScratch))
        {
            $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
            $scratchPath = [IO.Path]::GetFullPath($whitespaceScratch)
            if (!$scratchPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
                [IO.Path]::GetFileName($scratchPath) -notmatch '^StudioX-source-style-[a-f0-9]{32}$')
            {
                throw 'Source style scratch cleanup path is invalid.'
            }
            Remove-Item -LiteralPath $scratchPath -Recurse -Force
        }
    }
    finally
    {
        if ($BuildArtifactsDirectory)
        {
            $env:ArtifactsPath = $previousArtifactsPath
            $env:UseArtifactsOutput = $previousArtifactsOutput
        }
    }
}
