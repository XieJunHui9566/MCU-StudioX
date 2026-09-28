param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [string]$BuildArtifactsDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildArguments = @()
if ($BuildArtifactsDirectory)
{
    # 隔离构建目录避免验证时覆盖正在运行的工作台程序集。
    $buildArguments = @('--artifacts-path', [IO.Path]::GetFullPath($BuildArtifactsDirectory))
}
& dotnet build (Join-Path $projectRoot 'StudioX.slnx') --configuration $Configuration --nologo @buildArguments
if ($LASTEXITCODE -ne 0)
{
    throw 'StudioX build failed.'
}
