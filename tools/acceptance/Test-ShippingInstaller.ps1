param([Parameter(Mandatory = $true)][string]$PlanPath)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
. (Join-Path $PSScriptRoot 'ShippingInstaller-Common.ps1')
. (Join-Path $PSScriptRoot 'Power-Guard.ps1')
$plan = Read-AcceptanceJson $PlanPath
$root = [IO.Path]::GetFullPath($plan.guestRoot).TrimEnd('\')
if ($root -ne 'C:\StudioX-Installer-20261004' -or $env:COMPUTERNAME -ne $plan.guestMachine -or ![Environment]::Is64BitProcess)
{
    throw 'Select the explicitly identified 64-bit Windows guest.'
}
$computer = Get-CimInstance Win32_ComputerSystem
if ($computer.Manufacturer -notmatch 'VMware')
{
    throw 'Shipping installer validation is restricted to the selected VMware guest.'
}
$work = Join-Path $root 'work';
$evidence = Join-Path $root 'evidence';
$logs = Join-Path $evidence 'logs'
foreach ($path in @($root, $work, $evidence, $logs))
{
    [IO.Directory]::CreateDirectory($path) | Out-Null;
    if ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)
    {
        throw 'Acceptance roots must be ordinary directories.'
    }
}
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B8050FBC-2DE2-43F4-B839-F0E90522E671}_is1'
$baselinePath = Join-Path $evidence 'baseline-before.json'
$installA = Join-Path $work 'Installed Light';
$installB = Join-Path $work 'Upgraded Full'
$data = Join-Path $work 'data';
$projects = Join-Path $work 'projects'
$probe = Join-Path $work 'probe/StudioX.ShippingInstallerValidation.exe'
$light = Join-Path $work ('inputs/' + $plan.light.file)
$full = Join-Path $work ('inputs/' + $plan.full.file)
$old = Join-Path $work ('inputs/' + $plan.previous.file)
$armArchive = Join-Path $work ('inputs/' + $plan.arm.file)
$statePath = Join-Path $root 'suite-state.json'
$script:phase = 'preflight';
$script:complete = $false;
$script:passed = $false;
$script:failure = $null
$script:checks = @();
$script:completed = @()
if (Test-Path -LiteralPath $statePath)
{
    $saved = Read-AcceptanceJson $statePath;
    if ($saved.passed -and $saved.complete)
    {
        Write-Host 'Shipping suite already completed; its evidence is preserved.';
        return
    };
    $script:checks = @($saved.checks);
    $script:completed = @($saved.completed)
}
function Publish-SuiteStatus
{
    Write-AcceptanceJson $statePath @{formatVersion =1;
        updatedUtc                                  =[DateTime]::UtcNow.ToString('o');
        phase                                       =$script:phase;
        complete                                    =$script:complete;
        passed                                      =$script:passed;
        failure                                     =$script:failure;
        completed                                   =$script:completed;
        checks                                      =$script:checks
    }
    if (Test-Path -LiteralPath $plan.exchange)
    {
        [IO.File]::Copy($statePath, (Join-Path $plan.exchange 'suite-state.json'), $true)
    }
}
function Check([bool]$Condition, [string]$Description)
{
    if (!$Condition)
    {
        throw $Description
    }
    $script:checks += , @{phase =$script:phase;
        description             =$Description
    }
    Write-Host ('PASS ' + $Description)
}
function Stage([string]$Name, [scriptblock]$Action)
{
    if ($Name -in $script:completed)
    {
        Write-Host ('Completed stage retained: ' + $Name);
        return
    }
    $script:phase = $Name;
    Publish-SuiteStatus
    & $Action
    $script:completed += , $Name;
    Publish-SuiteStatus
}
function Run([string]$File, [string[]]$Arguments, [string]$Label, [int]$Timeout = 3600)
{
    Invoke-AcceptanceProcess $File $Arguments (Join-Path $logs $Label) $Timeout { Publish-SuiteStatus }
}
function Probe([string[]]$Arguments, [string]$Label)
{
    $code = Run $probe $Arguments $Label
    if ($code -ne 0)
    {
        throw ('Installed-service probe failed: ' + $Label + '; exit ' + $code)
    }
}
function Json-Result([string]$Name)
{
    Join-Path $evidence ($Name + '.json')
}
function Setup-Args([string]$Directory, [string]$Label)
{
    $log = Join-Path $logs ($Label + '.setup.log');
    if (Test-Path -LiteralPath $log)
    {
        $log += '-retry-' + [Guid]::NewGuid().ToString('N')
    };
    @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/NOICONS', ('/DIR=' + $Directory), ('/LOG=' + $log))
}
function Install([string]$Installer, [string]$Directory, [string]$Version, [string]$Label)
{
    if (Test-Path -LiteralPath $registry)
    {
        $existing = (Get-ItemProperty -LiteralPath $registry).'Inno Setup: App Path';
        if ([IO.Path]::GetFullPath($existing).TrimEnd('\') -ne $Directory)
        {
            throw 'An installation outside the active private fixture exists; preserve it.'
        }
    }
    $code = Run $Installer (Setup-Args $Directory $Label) $Label
    Check ($code -eq 0) ($Label + ' exits successfully')
    Check ((Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $Version) ($Label + ' registers the expected version')
    Check ((Get-Item -LiteralPath (Join-Path $Directory 'MCU StudioX.exe')).VersionInfo.ProductVersion.Trim() -eq $Version) ($Label + ' installs the expected executable')
}
function Installed-Pack([string]$Directory, [string]$Id)
{
    $index = Read-AcceptanceJson (Join-Path $Directory 'device-packs/index.json')
    $match = @($index | Where-Object { $_.id -eq $Id })
    if ($match.Count -ne 1)
    {
        throw ('Shipping pack identity must be unique: ' + $Id)
    }
    $base = [IO.Path]::GetFullPath((Join-Path $Directory 'device-packs'))
    $path = [IO.Path]::GetFullPath((Join-Path $base $match[0].file))
    if (!$path.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $path -PathType Leaf))
    {
        throw 'Shipping pack path escaped its catalog.'
    }
    return $path
}
function Smoke([string]$Directory, [string]$Label)
{
    $output = Join-Path $evidence $Label
    $code = Run (Join-Path $Directory 'MCU StudioX.exe') @('--smoke', $output) $Label 180
    Check ($code -eq 0 -and (Test-Path -LiteralPath (Join-Path $output 'result.txt'))) ($Label + ' starts the actual self-contained desktop')
}
function Uninstall([string]$Directory, [string]$Label)
{
    Check ((Get-ItemProperty -LiteralPath $registry).'Inno Setup: App Path' -eq $Directory) ($Label + ' targets only the private installation')
    $code = Run (Join-Path $Directory 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG=' + (Join-Path $logs ($Label + '.setup.log')))) $Label
    Check ($code -eq 0 -and !(Test-Path -LiteralPath $registry) -and !(Test-Path -LiteralPath (Join-Path $Directory 'MCU StudioX.exe'))) ($Label + ' removes application registration and executable')
}
$guard = Start-AcceptancePowerRequest
try
{
    if (!(Test-Path -LiteralPath $baselinePath))
    {
        if (Test-Path -LiteralPath $registry)
        {
            throw 'The guest already has a product installation; do not replace it.'
        }
        $os = Get-CimInstance Win32_OperatingSystem;
        $disk = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:'"
        if ($disk.FreeSpace -lt 55GB)
        {
            throw 'Guest needs at least 55 GiB free for the two retained shipping-installation fixtures.'
        }
        $userData = Join-Path $env:LOCALAPPDATA 'MCUStudioX'
        $userDataExisted = Test-Path -LiteralPath $userData
        $sentinel = Join-Path $userData ('installer-acceptance-' + [Guid]::NewGuid().ToString('N') + '.txt')
        $preferences = @(foreach ($name in @('preferences.json', 'appearance.json', 'editor.json', 'recent-projects.json'))
            {
                $path = Join-Path $userData $name; if (Test-Path -LiteralPath $path)
                {
                    @{path = $path; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
                }
            })
        $commands = @(foreach ($name in @('dotnet', 'python', 'gcc', 'cmake', 'ninja', 'clangd', 'git'))
            {
                @{name = $name; paths = @(Get-Command $name -All -ErrorAction SilentlyContinue | ForEach-Object { $_.Source }) }
            })
        Write-AcceptanceJson $baselinePath @{machine =$env:COMPUTERNAME;
            os                                       =$os.Caption;
            osVersion                                =$os.Version;
            powershell                               =$PSVersionTable.PSVersion.ToString();
            computer                                 =$computer.Model;
            freeBytes                                =$disk.FreeSpace;
            environment                              =(Get-AcceptanceEnvironment);
            productInstalled                         =$false;
            userData                                 =$userData;
            userDataExisted                          =$userDataExisted;
            sentinel                                 =$sentinel;
            preferences                              =$preferences;
            commands                                 =$commands;
            createdUtc                               =[DateTime]::UtcNow.ToString('o')
        }
    }
    $baseline = Read-AcceptanceJson $baselinePath
    [IO.Directory]::CreateDirectory($baseline.userData) | Out-Null
    if (!(Test-Path -LiteralPath $baseline.sentinel))
    {
        [IO.File]::WriteAllText($baseline.sentinel, 'installer acceptance preserves user data')
    }
    Stage '01-fresh-light' {
        Install $light $installA $plan.version 'fresh-light'
        Probe @('payload', $installA, (Json-Result 'fresh-light-payload')) 'fresh-light-payload'
        Check (!(Test-Path -LiteralPath (Join-Path $installA 'runtime/toolsets'))) 'fresh light installer contains no preinstalled development components'
        Smoke $installA 'fresh-light-ui'
    }
    $armProject = Join-Path $projects 'arm_first'
    Stage '02-create-missing-component-project' {
        Probe @('create', $installA, (Installed-Pack $installA 'studiox.stm32f407'), 'STM32F407ZG', 'hal', 'arm_first', $armProject, $data, (Json-Result 'arm-created')) 'arm-created'
        $created = Read-AcceptanceJson (Json-Result 'arm-created')
        Check (@($created.requirements.requirements | Where-Object { $_.state -ne 'missing' }).Count -eq 0 -and $created.requirements.requirements.Count -gt 0) 'fresh light project identifies its exact missing component'
    }
    Stage '02-import-selected-component' {
        Probe @('import', $installA, $armArchive, $data, (Json-Result 'arm-imported')) 'arm-imported'
    }
    Stage '02-build-light-project' {
        Probe @('build', $installA, $armProject, $data, (Json-Result 'arm-built-light')) 'arm-built-light'
        Check ((Read-AcceptanceJson (Json-Result 'arm-built-light')).passed) 'light installation creates and actually compiles an ARM project after explicit component import'
    }
    Stage '03-first-project-ui' {
        $output = Join-Path $evidence 'first-project-ui'
        $code = Run (Join-Path $installA 'MCU StudioX.exe') @('--preview-first-project', $output, (Installed-Pack $installA 'studiox.stm32f103')) 'first-project-ui' 900
        Check ($code -eq 0 -and (Read-AcceptanceJson (Join-Path $output 'result.json')).status -eq 'passed') 'actual installed first-project guide creates, edits, saves and builds firmware through the WPF workflow'
    }
    Stage '04-light-to-full' {
        Probe @('snapshot', (Join-Path $installA 'runtime/toolsets/arm.gnu/1.0.0'), (Json-Result 'arm-before-full')) 'arm-before-full'
        [IO.File]::WriteAllText((Join-Path $installA 'user-added-note.txt'), 'preserve unowned files')
        Install $full $installA $plan.version 'light-to-full'
        Probe @('payload', $installA, (Json-Result 'full-payload'), 'runtime/toolsets/arm.gnu/1.0.0/') 'full-payload'
        Probe @('compare', (Join-Path $installA 'runtime/toolsets/arm.gnu/1.0.0'), (Json-Result 'arm-before-full'), (Json-Result 'arm-preserved-by-full')) 'arm-preserved-by-full'
        Smoke $installA 'full-ui'
    }
    $espProject = Join-Path $projects 'esp_first'
    Stage '05-full-esp-project' {
        if (!(Test-AcceptanceCreatedProject $espProject (Json-Result 'esp-created') $installA 'espressif.esp32c3' 'ESP32-C3' 'hello-world' 'esp_first' (Json-Result 'esp-created-resumed')))
        {
            Probe @('create', $installA, (Installed-Pack $installA 'espressif.esp32c3'), 'ESP32-C3', 'hello-world', 'esp_first', $espProject, $data, (Json-Result 'esp-created')) 'esp-created'
        }
        $created = Read-AcceptanceJson (Json-Result 'esp-created')
        Check ($created.manifest.espressif.sdkVersion -eq '5.5.4') 'shipping ESP32-C3 template selects its declared preinstalled IDF version'
        Probe @('build', $installA, $espProject, $data, (Json-Result 'esp-built-full')) 'esp-built-full'
        Check ((Read-AcceptanceJson (Json-Result 'esp-built-full')).passed) 'full installation actually compiles ESP32-C3 with its bundled IDF'
    }
    Stage '06-installation-guards' {
        $mutex = [Threading.Mutex]::new($false, 'MCUStudioX.Desktop.InstallLock')
        try
        {
            $code = Run $light (Setup-Args $installA 'running-app-blocked') 'running-app-blocked' 60;
            Check ($code -ne 0) 'running IDE mutex blocks installation without force-closing it'
        }
        finally
        {
            $mutex.Dispose()
        }
        $code = Run $old (Setup-Args $installA 'actual-old-downgrade-blocked') 'actual-old-downgrade-blocked' 60
        Check ($code -ne 0 -and (Get-ItemProperty -LiteralPath $registry).DisplayVersion -eq $plan.version) 'actual 0.2.5.4B shipping installer refuses downgrade over 0.2.6.10'
        $wrong = Join-Path $work 'Wrong Location'
        $code = Run $light (Setup-Args $wrong 'changed-location-blocked') 'changed-location-blocked' 60
        Check ($code -ne 0 -and !(Test-Path -LiteralPath (Join-Path $wrong 'MCU StudioX.exe'))) 'upgrade refuses to change the installation directory'
    }
    Stage '07-full-to-light-and-repair' {
        foreach ($name in @('toolsets', 'hdl', 'stc-isp'))
        {
            Probe @('snapshot', (Join-Path $installA ('runtime/' + $name)), (Json-Result ('a-' + $name + '-before'))) ('a-' + $name + '-before')
        }
        Probe @('snapshot', $projects, (Json-Result 'projects-before-switch')) 'projects-before-switch'
        Install $light $installA $plan.version 'full-to-light'
        [IO.File]::WriteAllText((Join-Path $installA 'release.json'), 'intentional repair fixture')
        Install $light $installA $plan.version 'same-version-repair'
        Probe @('payload', $installA, (Json-Result 'repaired-light-payload')) 'repaired-light-payload'
        foreach ($name in @('toolsets', 'hdl', 'stc-isp'))
        {
            Probe @('compare', (Join-Path $installA ('runtime/' + $name)), (Json-Result ('a-' + $name + '-before')), (Json-Result ('a-' + $name + '-after-switch'))) ('a-' + $name + '-after-switch')
        }
        Probe @('compare', $projects, (Json-Result 'projects-before-switch'), (Json-Result 'projects-after-switch')) 'projects-after-switch'
        Check ((Read-AcceptanceJson (Join-Path $installA 'release.json')).distributionProfile -eq 'light') 'full-to-light switch and same-version repair restore the declared light payload'
    }
    Stage '08-uninstall-light-preserves-data' {
        Uninstall $installA 'uninstall-light'
        foreach ($name in @('toolsets', 'hdl', 'stc-isp'))
        {
            Probe @('compare', (Join-Path $installA ('runtime/' + $name)), (Json-Result ('a-' + $name + '-before')), (Json-Result ('a-' + $name + '-after-uninstall'))) ('a-' + $name + '-after-uninstall')
        }
        Probe @('compare', $projects, (Json-Result 'projects-before-switch'), (Json-Result 'projects-after-uninstall')) 'projects-after-uninstall'
        Check ((Test-Path -LiteralPath (Join-Path $installA 'user-added-note.txt')) -and (Test-Path -LiteralPath $baseline.sentinel)) 'uninstall preserves unowned installation files and independent user data'
    }
    $oldProject = Join-Path $projects 'previous_firmware'
    Stage '09-actual-previous-version' {
        $previousReady = $false
        $installReceipt = Join-Path $logs 'actual-previous-version.process.json'
        $smokeReceipt = Join-Path $logs 'previous-version-ui.process.json'
        if ((Test-Path -LiteralPath $registry) -and (Test-Path -LiteralPath $installReceipt) -and (Test-Path -LiteralPath $smokeReceipt))
        {
            $registration = Get-ItemProperty -LiteralPath $registry
            $installedReceipt = Read-AcceptanceJson $installReceipt;
            $startedReceipt = Read-AcceptanceJson $smokeReceipt
            $previousReady = $registration.'Inno Setup: App Path' -eq $installB -and $registration.DisplayVersion -eq $plan.previousVersion -and $installedReceipt.complete -and $installedReceipt.exitCode -eq 0 -and $installedReceipt.file -eq $old -and $startedReceipt.complete -and $startedReceipt.exitCode -eq 0 -and $startedReceipt.file -eq (Join-Path $installB 'MCU StudioX.exe') -and (Test-Path -LiteralPath (Join-Path $evidence 'previous-version-ui/result.txt'))
        }
        if ($previousReady)
        {
            Check ((Get-Item -LiteralPath (Join-Path $installB 'MCU StudioX.exe')).VersionInfo.ProductVersion.Trim() -eq $plan.previousVersion) 'resume reuses the already verified actual previous installation and desktop start'
        }
        else
        {
            Install $old $installB $plan.previousVersion 'actual-previous-version'
            Smoke $installB 'previous-version-ui'
        }
        Probe @('create', $installB, (Installed-Pack $installB 'studiox.stm32f407'), 'STM32F407ZG', 'hal', 'previous_firmware', $oldProject, $data, (Json-Result 'old-project-created')) 'old-project-created'
        Probe @('build', $installB, $oldProject, $data, (Json-Result 'old-project-built')) 'old-project-built'
        foreach ($name in @('toolsets', 'hdl', 'stc-isp'))
        {
            Probe @('snapshot', (Join-Path $installB ('runtime/' + $name)), (Json-Result ('b-' + $name + '-before'))) ('b-' + $name + '-before')
        }
        Probe @('snapshot', $oldProject, (Json-Result 'old-project-before-upgrade')) 'old-project-before-upgrade'
    }
    Stage '10-actual-upgrade-to-current' {
        Install $full $installB $plan.version 'actual-upgrade-to-current'
        Probe @('payload', $installB, (Json-Result 'upgraded-program-payload'), 'runtime/toolsets/', 'runtime/hdl/', 'runtime/stc-isp/') 'upgraded-program-payload'
        foreach ($name in @('toolsets', 'hdl', 'stc-isp'))
        {
            Probe @('compare', (Join-Path $installB ('runtime/' + $name)), (Json-Result ('b-' + $name + '-before')), (Json-Result ('b-' + $name + '-after-upgrade'))) ('b-' + $name + '-after-upgrade')
        }
        Probe @('compare', $oldProject, (Json-Result 'old-project-before-upgrade'), (Json-Result 'old-project-after-upgrade')) 'old-project-after-upgrade'
        Probe @('build', $installB, $oldProject, $data, (Json-Result 'old-project-rebuilt-current')) 'old-project-rebuilt-current'
        Smoke $installB 'upgraded-current-ui'
    }
    Stage '11-uninstall-upgraded-full' {
        Uninstall $installB 'uninstall-upgraded-full'
        foreach ($name in @('toolsets', 'hdl', 'stc-isp'))
        {
            Probe @('compare', (Join-Path $installB ('runtime/' + $name)), (Json-Result ('b-' + $name + '-before')), (Json-Result ('b-' + $name + '-after-uninstall'))) ('b-' + $name + '-after-uninstall')
        }
        Check ((Test-Path -LiteralPath (Join-Path $oldProject '.studiox/project.json')) -and (Test-Path -LiteralPath $baseline.sentinel)) 'upgraded full uninstall preserves prior firmware project and user data'
    }
    Stage '12-final-environment' {
        $after = Get-AcceptanceEnvironment
        Assert-AcceptanceEnvironment $baseline.environment $after
        foreach ($item in $baseline.preferences)
        {
            Check ((Get-FileHash -LiteralPath $item.path -Algorithm SHA256).Hash -eq $item.sha256) ('existing user preference preserved: ' + [IO.Path]::GetFileName($item.path))
        }
        Check (!(Test-Path -LiteralPath $registry)) 'final official product registration is absent'
        Check ([IO.File]::ReadAllText($baseline.sentinel) -eq 'installer acceptance preserves user data') 'independent user-data marker remains unchanged'
        Remove-Item -LiteralPath $baseline.sentinel
        if (!$baseline.userDataExisted -and !@(Get-ChildItem -LiteralPath $baseline.userData -Force).Count)
        {
            Remove-Item -LiteralPath $baseline.userData
        }
        Write-AcceptanceJson (Json-Result 'baseline-after') @{environment =$after;
            productInstalled                                              =$false;
            persistentEnvironmentUnchanged                                =$true;
            createdUtc                                                    =[DateTime]::UtcNow.ToString('o')
        }
    }
    $script:passed = $true;
    $script:complete = $true;
    $script:phase = 'complete'
    Write-AcceptanceJson (Json-Result 'result') @{formatVersion =1;
        passed                                                  =$true;
        version                                                 =$plan.version;
        previousVersion                                         =$plan.previousVersion;
        actualShippingInstallers                                =$true;
        cleanWindowsGuest                                       =$true;
        hardware                                                =$false;
        downloadedSdk                                           =$false;
        completed                                               =$script:completed;
        checks                                                  =$script:checks;
        plan                                                    =$plan;
        completedUtc                                            =[DateTime]::UtcNow.ToString('o')
    }
}
catch
{
    $originalFailure = $_
    $script:failure = $_.Exception.ToString() + [Environment]::NewLine + $_.ScriptStackTrace;
    $script:complete = $true
    Publish-SuiteStatus
    # 失败时也回传原始安装器/编译器日志，保留来宾现场供同一入口续跑。
    try
    {
        $label = 'failed-evidence-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmss') + '-' + [Guid]::NewGuid().ToString('N')
        $failedZip = Join-Path $root ($label + '.zip');
        $failedExport = Join-Path $root ($label + '.json')
        $code = Invoke-AcceptanceProcess $probe @('export', $evidence, $failedZip, $failedExport) (Join-Path $root $label) 600
        if ($code -ne 0)
        {
            throw 'Failed-stage evidence export did not complete.'
        }
        $hostZip = Join-Path $plan.exchange ($label + '.zip')
        [IO.File]::Copy($failedZip, $hostZip, $false)
        if ((Get-FileHash -LiteralPath $hostZip -Algorithm SHA256).Hash -ne (Read-AcceptanceJson $failedExport).sha256)
        {
            throw 'Failed-stage host evidence differs from its guest export.'
        }
        [IO.File]::Copy($failedExport, (Join-Path $plan.exchange ($label + '.json')), $false)
    }
    catch
    {
        Write-Warning ('Original failure preserved; diagnostic export also failed: ' + $_.Exception.ToString())
    }
    throw $originalFailure
}
finally
{
    Publish-SuiteStatus;
    Stop-AcceptancePowerRequest $guard
}
