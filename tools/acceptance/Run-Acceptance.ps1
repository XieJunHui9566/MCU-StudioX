param([string]$OutputDirectory, [string]$ResumeDirectory, [switch]$ShowWorkbench)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$kit = [IO.Path]::GetFullPath($PSScriptRoot)
. (Join-Path $kit 'Power-Guard.ps1')
function KitPath([string]$Relative)
{
    if ([IO.Path]::IsPathRooted($Relative))
    {
        throw 'Kit paths must be relative.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $kit $Relative))
    if (!$path.StartsWith($kit.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase))
    {
        throw 'Kit path escaped its root.'
    }
    $cursor = $path
    while ($cursor -and $cursor.Length -ge $kit.Length)
    {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint))
        {
            throw 'Kit links are not supported.'
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    return $path
}
$manifest = Get-Content -LiteralPath (Join-Path $kit 'kit-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.formatVersion -ne 1)
{
    throw 'Unsupported acceptance kit.'
}
Write-Host 'Verifying the complete portable payload (including offline archives)...'
foreach ($entry in $manifest.files)
{
    $path = KitPath $entry.path
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256)
    {
        throw ('Payload changed: ' + $entry.path)
    }
}
if ($ResumeDirectory -and $OutputDirectory)
{
    throw 'Choose a new output or explicitly resume, not both.'
}
if ($ResumeDirectory)
{
    $output = [IO.Path]::GetFullPath($ResumeDirectory)
    if (!(Test-Path -LiteralPath (Join-Path $output 'projects')))
    {
        throw 'Resume requires an existing acceptance fixture.'
    }
}
else
{
    if (!$OutputDirectory)
    {
        $OutputDirectory = Join-Path $kit ('evidence/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    }
    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (Test-Path -LiteralPath $output)
    {
        throw 'Use a new output directory; existing evidence is preserved.'
    }
    # 新验收从空目录导入；不复用安装机 SDK、系统 PATH 或用户的正常 IDE 数据。
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output)) | Out-Null
}
$runner = KitPath 'runner/StudioX.EnvironmentReliabilityValidation.exe'
$plan = KitPath 'inputs/plan.json'
$mode = if ($ResumeDirectory)
{
    '--offline-resume'
}
else
{
    '--offline'
}
$runId = [Guid]::NewGuid().ToString('N')
$logDirectory = Join-Path ([IO.Path]::GetDirectoryName($output)) ('acceptance-logs-' + $runId)
[IO.Directory]::CreateDirectory($logDirectory) | Out-Null
$passed = $false
$powerRequest = Start-AcceptancePowerRequest
try
{
    & $runner $mode $plan $output 2>&1 | Tee-Object -FilePath (Join-Path $logDirectory 'offline.log')
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Offline acceptance failed; preserve logs and use explicit resume after resolving the cause.'
    }
    $offline = Get-Content -LiteralPath (Join-Path $output 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$offline.passed)
    {
        throw 'Offline result did not pass.'
    }
    $ninja = Join-Path $output 'selected installation/runtime/toolsets/espressif.idf/6.1.0/ninja/ninja.exe'
    if (!(Test-Path -LiteralPath $ninja))
    {
        throw 'The kit requires its explicitly selected IDF 6.1 Ninja for fault fixtures.'
    }
    $boundaries = Join-Path $output ('recovery-faults-' + $runId)
    & $runner --boundaries $boundaries $ninja 2>&1 | Tee-Object -FilePath (Join-Path $logDirectory 'recovery.log')
    if ($LASTEXITCODE -ne 0)
    {
        throw 'Recovery fault acceptance failed.'
    }
    $recovery = Get-Content -LiteralPath (Join-Path $boundaries 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    if (!$recovery.passed)
    {
        throw 'Recovery result did not pass.'
    }
    $desktop = KitPath 'desktop/MCU StudioX.exe'
    $ui = Join-Path $output ('desktop-smoke-' + $runId)
    $process = Start-Process -FilePath $desktop -ArgumentList @('--smoke', ('"' + $ui + '"')) -WindowStyle Hidden -PassThru
    try
    {
        if (!$process.WaitForExit(45000))
        {
            $process.Kill();
            throw 'Desktop smoke timed out.'
        }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $ui 'result.txt')))
        {
            throw 'Self-contained desktop smoke failed.'
        }
    }
    finally
    {
        $process.Dispose()
    }
    $toolUi = Join-Path $output ('component-ui-' + $runId)
    $runtime = Join-Path $output 'selected installation/runtime'
    $project = Join-Path $output 'projects/esp610'
    $process = Start-Process -FilePath $desktop -ArgumentList @('--preview-tool-management', ('"' + $toolUi + '"'), ('"' + $project + '"'), ('"' + $runtime + '"')) -WindowStyle Hidden -PassThru
    try
    {
        if (!$process.WaitForExit(60000))
        {
            $process.Kill();
            throw 'Component UI preview timed out.'
        }
        if ($process.ExitCode -ne 0)
        {
            throw 'Component UI preview failed.'
        }
        $uiResult = Get-Content -LiteralPath (Join-Path $toolUi 'tool-management-ui-result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($uiResult.status -ne 'passed')
        {
            throw 'Component UI did not pass.'
        }
    }
    finally
    {
        $process.Dispose()
    }
    $passed = $true
    Write-Host ('PASS. Evidence: ' + $output)
    if ($ShowWorkbench)
    {
        $data = Join-Path $output ('workbench-data-' + $runId)
        [IO.Directory]::CreateDirectory($data) | Out-Null
        Copy-Item -LiteralPath (Join-Path $output 'packs') -Destination (Join-Path $data 'packs') -Recurse
        $hostSource = KitPath 'desktop/plugin-host'
        $hostTarget = Join-Path $runtime 'plugin-host'
        foreach ($file in Get-ChildItem -LiteralPath $hostSource -File -Recurse)
        {
            $target = Join-Path $hostTarget $file.FullName.Substring($hostSource.Length + 1)
            if (Test-Path -LiteralPath $target)
            {
                if ((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $target).Hash)
                {
                    throw 'Existing acceptance plugin host differs; preserve it and use a new fixture.'
                }
            }
            else
            {
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
                [IO.File]::Copy($file.FullName, $target, $false)
            }
        }
        # 只为明确要求的可见工作台开窗；不打开串口或自动下载固件。
        Start-Process -FilePath $desktop -ArgumentList @('--acceptance-workbench', ('"' + $runtime + '"'), ('"' + $data + '"')) | Out-Null
    }
}
finally
{
    Stop-AcceptancePowerRequest $powerRequest
    @{ formatVersion         =1;
        passed               =$passed;
        resumed              =[bool]$ResumeDirectory;
        machine              =$env:COMPUTERNAME;
        os                   =[Environment]::OSVersion.VersionString;
        cleanWindowsVmTested =$false;
        hardware             =$false;
        downloadedSdk        =$false;
        kitManifestSha256    =(Get-FileHash -LiteralPath (Join-Path $kit 'kit-manifest.json') -Algorithm SHA256).Hash;
        evidence             =$output;
        logs                 =$logDirectory
    } | ConvertTo-Json -Depth 6 |
        Set-Content -LiteralPath (Join-Path $logDirectory 'acceptance.json') -Encoding UTF8
}
