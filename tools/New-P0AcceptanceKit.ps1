param(
    [Parameter(Mandatory = $true)][string]$RunnerDirectory,
    [Parameter(Mandatory = $true)][string]$DesktopDirectory,
    [Parameter(Mandatory = $true)][string]$PluginHostDirectory,
    [Parameter(Mandatory = $true)][string]$InputsDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Use a new kit directory; existing artifacts are preserved.'
}
$inputs = [IO.Path]::GetFullPath($InputsDirectory)
$plan = Get-Content -LiteralPath (Join-Path $inputs 'plan.json') -Raw | ConvertFrom-Json
foreach ($path in @($plan.archives) + @($plan.languagesDirectory) + @($plan.cases | ForEach-Object { $_.pack }))
{
    if ([IO.Path]::IsPathRooted($path) -or $path -match '(^|[\\/])\.\.([\\/]|$)')
    {
        throw 'Portable inputs must use paths relative to plan.json.'
    }
}
function Copy-Tree([string]$Source, [string]$Destination)
{
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd('\')
    if (!(Test-Path -LiteralPath $sourceRoot -PathType Container) -or $output.StartsWith($sourceRoot + '\', [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Invalid source or nested output.'
    }
    [IO.Directory]::CreateDirectory($Destination) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Force)
    {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
        {
            throw ('Payload links are not supported: ' + $item.FullName)
        }
        if (!$item.PSIsContainer)
        {
            $relative = $item.FullName.Substring($sourceRoot.Length + 1)
            $target = Join-Path $Destination $relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
            [IO.File]::Copy($item.FullName, $target, $false)
        }
    }
}
[IO.Directory]::CreateDirectory($output) | Out-Null
Copy-Tree $RunnerDirectory (Join-Path $output 'runner')
Copy-Tree $DesktopDirectory (Join-Path $output 'desktop')
# 独立验收运行目录通过显式参数注入；插件宿主仍使用应用服务既有安装目录后备入口。
Copy-Tree $PluginHostDirectory (Join-Path $output 'desktop/plugin-host')
Copy-Tree $InputsDirectory (Join-Path $output 'inputs')
foreach ($name in @('Run-Acceptance.ps1', 'Power-Guard.ps1', 'Run-Acceptance.cmd', 'README.md'))
{
    $source = Join-Path $PSScriptRoot ('acceptance/' + $name)
    $target = Join-Path $output $name
    # PowerShell 5.1 会按系统代码页解析无 BOM 脚本，中文注释可能吞掉下一行。
    if ($name.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase))
    {
        [IO.File]::WriteAllText($target, [IO.File]::ReadAllText($source), [Text.UTF8Encoding]::new($true))
    }
    else
    {
        [IO.File]::Copy($source, $target, $false)
    }
}
$files = @(Get-ChildItem -LiteralPath $output -File -Recurse | Sort-Object FullName | ForEach-Object {
        @{ path = $_.FullName.Substring($output.Length + 1).Replace('\', '/'); bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw 'Unable to record source commit.'
}
$dirty = @(& git -C $root status --porcelain=v1).Count -gt 0
$sourcePaths = @(& rg --files (Join-Path $root 'src') (Join-Path $root 'tools') (Join-Path $root 'docs') -g '*.cs' -g '*.csproj' -g '*.xaml' -g '*.ps1' -g '*.md')
if ($LASTEXITCODE -ne 0)
{
    throw 'Unable to bind source inventory.'
}
$sources = @($sourcePaths | Sort-Object | ForEach-Object { @{ path = $_.Substring($root.Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } })
@{ formatVersion         =1;
    createdUtc           =[DateTime]::UtcNow.ToString('o');
    kind                 ='private-portable-acceptance';
    sourceCommit         =$head;
    sourceDirty          =$dirty;
    cleanWindowsVmTested =$false;
    hardware             =$false;
    downloadedSdk        =$false;
    files                =$files;
    sourceFiles          =$sources
} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'kit-manifest.json') -Encoding UTF8
Write-Host ('Acceptance kit: ' + $output + '; ' + $files.Count + ' files; ' + ($files | Measure-Object -Property bytes -Sum).Sum + ' bytes.')
