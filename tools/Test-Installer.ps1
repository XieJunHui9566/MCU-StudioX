param(
    [Parameter(Mandatory=$true)][string]$Installer,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$PreviousVersion = '0.1.0',
    [string]$CompilerPath = (Join-Path $PSScriptRoot '../.artifacts/installer-tools/InnoSetup-7.1.0/ISCC.exe')
)
$ErrorActionPreference = 'Stop'
$Installer = [IO.Path]::GetFullPath($Installer)
$releaseVersion = ([version](Get-Item -LiteralPath $Installer).VersionInfo.FileVersion).ToString(3)
if ($PreviousVersion -notmatch '^\d+\.\d+\.\d+$' -or [version]$PreviousVersion -ge [version]$releaseVersion) { throw 'PreviousVersion must be lower than the installer version.' }
$nextVersion = [version]$releaseVersion
$newerVersion = '{0}.{1}.{2}' -f $nextVersion.Major, $nextVersion.Minor, ($nextVersion.Build + 1)
$root = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Choose a new validation directory.' }
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B8050FBC-2DE2-43F4-B839-F0E90522E671}_is1'
if (Test-Path -LiteralPath $registry) { throw 'MCU StudioX is already installed for this user; do not alter an existing installation during validation.' }
New-Item -ItemType Directory -Path $root | Out-Null
$installed = Join-Path $root 'Installed App'
$log = [Collections.Generic.List[string]]::new()
function Pass([string]$message) { $log.Add('PASS ' + $message); Write-Output "PASS $message"; $log | Set-Content -LiteralPath (Join-Path $root 'result.txt') -Encoding utf8 }
function Assert([bool]$condition, [string]$message) { if (!$condition) { throw $message } }
function Run-Program([string]$file, [string[]]$arguments, [int]$timeoutSeconds=300) {
    $start = [Diagnostics.ProcessStartInfo]::new($file)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds($timeoutSeconds)
    while (!$process.WaitForExit(1000)) {
        if ([DateTime]::UtcNow -gt $deadline) { $process.Kill($true); throw "Timed out: $file" }
    }
    return $process.ExitCode
}
function Setup-Arguments([string]$name, [string]$directory=$installed) {
    return @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-','/NOICONS',"/DIR=$directory",("/LOG=" + (Join-Path $root "$name.log")))
}

# 独立模拟旧版本，用相同产品标识和安装脚本验证真实覆盖升级流程。
$seed = Join-Path $root 'old-version-fixture'; New-Item -ItemType Directory -Path $seed | Out-Null
'OLD VERSION FIXTURE - NOT AN EXECUTABLE' | Set-Content -LiteralPath (Join-Path $seed 'MCU StudioX.exe')
$seedOutput = Join-Path $root 'old-installer'
& $CompilerPath --quiet --no-compression "--define=AppVersion=$PreviousVersion" "--define=PayloadDirectory=$seed" "--output-dir=$seedOutput" (Join-Path $PSScriptRoot 'installer/StudioX.iss')
if ($LASTEXITCODE -ne 0) { throw 'Cannot build the upgrade fixture.' }
$oldInstaller = Join-Path $seedOutput "MCU-StudioX-$PreviousVersion-win-x64-Setup.exe"
$exitCode = Run-Program $oldInstaller (Setup-Arguments 'install-old')
Assert ($exitCode -eq 0) "Old fixture installation failed: $exitCode"
Assert ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $PreviousVersion) 'Old version registration missing'
$userFile = Join-Path $installed 'user-added-note.txt'; 'preserve me' | Set-Content -LiteralPath $userFile
$data = Join-Path $env:LOCALAPPDATA 'MCUStudioX'
$sentinel = Join-Path $data ('installer-validation-' + [Guid]::NewGuid().ToString('N') + '.txt')
[IO.Directory]::CreateDirectory($data) | Out-Null
'preserve personal data' | Set-Content -LiteralPath $sentinel
$preferences = @{}
foreach ($name in @('preferences.json','appearance.json','editor.json','recent-projects.json')) {
    $path = Join-Path $data $name
    if (Test-Path -LiteralPath $path) { $preferences[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
}
Pass "$PreviousVersion fixture installed with the permanent product ID"
$exitCode = Run-Program $Installer (Setup-Arguments 'upgrade-to-current')
Assert ($exitCode -eq 0) "Upgrade failed: $exitCode"
Assert ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $releaseVersion) 'New version registration incorrect'
Assert ((Get-Item -LiteralPath (Join-Path $installed 'MCU StudioX.exe')).VersionInfo.FileVersion -eq "$releaseVersion.0") 'Executable version incorrect'
Pass 'Upgrade replaces old application and keeps one uninstall entry'

$mutex = [Threading.Mutex]::new($false, 'MCUStudioX.Desktop.InstallLock')
try {
    $exitCode = Run-Program $Installer (Setup-Arguments 'running-app-blocked') 30
    Assert ($exitCode -ne 0) 'Installer ignored a running IDE mutex'
} finally { $mutex.Dispose() }
Pass 'Running IDE blocks silent installation without force-closing it'
Set-ItemProperty -LiteralPath $registry -Name DisplayVersion -Value $newerVersion
try {
    $exitCode = Run-Program $Installer (Setup-Arguments 'downgrade-blocked') 30
    Assert ($exitCode -ne 0) 'Downgrade was allowed'
    Assert ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $newerVersion) 'Rejected downgrade changed registration'
} finally { Set-ItemProperty -LiteralPath $registry -Name DisplayVersion -Value $releaseVersion }
Pass 'Older installer refuses a newer registered version'
$wrongDirectory = Join-Path $root 'Wrong Location'
$exitCode = Run-Program $Installer (Setup-Arguments 'location-change-blocked' $wrongDirectory) 30
Assert ($exitCode -ne 0 -and !(Test-Path -LiteralPath (Join-Path $wrongDirectory 'MCU StudioX.exe'))) 'Silent upgrade moved the installation'
Pass 'Upgrade rejects a changed installation location'
'intentional repair check' | Set-Content -LiteralPath (Join-Path $installed 'release.json')
$exitCode = Run-Program $Installer (Setup-Arguments 'same-version-repair')
Assert ($exitCode -eq 0) "Repair failed: $exitCode"
Assert ((Get-Content -LiteralPath (Join-Path $installed 'release.json') -Raw | ConvertFrom-Json).version -eq $releaseVersion) 'Repair did not restore program files'
Pass 'Same-version reinstall repairs a modified program file'

$hashes = Get-Content -LiteralPath (Join-Path $installed 'release-files.sha256.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($relative in $hashes.Keys) {
    $path = Join-Path $installed $relative
    Assert ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $hashes[$relative]) "Installed file mismatch: $relative"
}
Pass "All $($hashes.Count) installed payload files match their SHA-256 values"
$packs = Get-Content -LiteralPath (Join-Path $installed 'device-packs/index.json') -Raw | ConvertFrom-Json
$minimumByVendor = @{ STMicroelectronics=23; AGM=1; WCH=2; Puya=16; GigaDevice=14 }
foreach ($vendor in $minimumByVendor.Keys) {
    $count = @($packs | Where-Object { $_.file.Replace('\', '/').StartsWith("$vendor/", [StringComparison]::OrdinalIgnoreCase) }).Count
    Assert ($count -ge $minimumByVendor[$vendor]) "Expected at least $($minimumByVendor[$vendor]) $vendor packs, got $count"
}
Assert (@($packs | Group-Object id | Where-Object Count -gt 1).Count -eq 0) 'Device pack index contains duplicate IDs'
Pass "$($packs.Count) indexed device packs are present under their manufacturers"
$smoke = Join-Path $root 'installed-desktop-smoke'
$exitCode = Run-Program (Join-Path $installed 'MCU StudioX.exe') @('--smoke', $smoke) 120
Assert ($exitCode -eq 0 -and (Test-Path -LiteralPath (Join-Path $smoke 'result.txt'))) 'Installed self-contained desktop did not start'
Pass 'Installed self-contained WPF application starts and renders both themes'

$uninstaller = Join-Path $installed 'unins000.exe'
$exitCode = Run-Program $uninstaller @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',("/LOG=" + (Join-Path $root 'uninstall.log')))
Assert ($exitCode -eq 0) "Uninstall failed: $exitCode"
Assert (!(Test-Path -LiteralPath $registry) -and !(Test-Path -LiteralPath (Join-Path $installed 'MCU StudioX.exe'))) 'Uninstall left program registration or executable'
Assert ((Test-Path -LiteralPath $userFile) -and (Test-Path -LiteralPath $sentinel)) 'Uninstall deleted unowned or personal data'
foreach ($path in $preferences.Keys) { Assert ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -eq $preferences[$path]) "Existing preferences changed: $path" }
Pass 'Uninstall removes owned program files, preserves added files, packs and existing preferences'
# 仅删除本次测试创建的唯一哨兵文件，绝不递归删除真实用户目录。
Remove-Item -LiteralPath $sentinel
Pass 'Installation, upgrade, repair, downgrade protection and uninstall validation completed'
