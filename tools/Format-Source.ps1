param([switch]$Check)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
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
& dotnet run --project (Join-Path $PSScriptRoot 'StudioX.SourceStyle') -- $mode $projectRoot
if ($LASTEXITCODE -ne 0)
{
    throw 'Structural source style check failed.'
}

# folder 模式不还原包；文件逐个列入，避免目录 include 在 SDK 中只匹配目录本身。
$absoluteFiles = @(& rg --files (Join-Path $projectRoot 'src') (Join-Path $projectRoot 'tools') (Join-Path $projectRoot 'examples/StudioX.SampleDecoder') -g '*.cs' -g '!**/bin/**' -g '!**/obj/**')
if ($LASTEXITCODE -ne 0)
{
    throw 'Cannot enumerate first-party C# files.'
}
$files = @($absoluteFiles | ForEach-Object { [IO.Path]::GetRelativePath($projectRoot, $_) })
# Windows 命令行有长度限制；分批只处理文件路径，不重复构建或复制运行时。
Push-Location -LiteralPath $projectRoot
try
{
    for ($offset = 0; $offset -lt $files.Count; $offset += 128)
    {
        $last = [Math]::Min($offset + 127, $files.Count - 1)
        $batch = $files[$offset..$last]
        $formatArguments = @('format', 'whitespace', $projectRoot, '--folder', '--include') + $batch + @('--exclude', 'artifacts', 'runtime', '.artifacts', '.git')
        if ($Check)
        {
            $formatArguments += '--verify-no-changes'
        }
        & dotnet @formatArguments
        if ($LASTEXITCODE -ne 0)
        {
            throw 'C# whitespace style check failed.'
        }
    }
}
finally
{
    Pop-Location
}
