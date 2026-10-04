$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
. (Join-Path $PSScriptRoot 'payload/scripts/ShippingInstaller-Common.ps1')
. (Join-Path $PSScriptRoot 'payload/scripts/Power-Guard.ps1')
. (Join-Path $PSScriptRoot 'payload/scripts/Copy-ResumeEntry.ps1')
. (Join-Path $PSScriptRoot 'payload/scripts/Clear-PrivateTree.ps1')
. (Join-Path $PSScriptRoot 'payload/scripts/ShippingInstaller-Cleanup.ps1')
$planPath = Join-Path $PSScriptRoot 'plan.json'
$plan = Read-AcceptanceJson $planPath
$root = [IO.Path]::GetFullPath($plan.guestRoot).TrimEnd('\')
if ($root -ne 'C:\StudioX-Installer-20261004' -or $env:COMPUTERNAME -ne $plan.guestMachine -or (Get-CimInstance Win32_ComputerSystem).Manufacturer -notmatch 'VMware')
{
    throw 'This launcher is limited to the selected Windows guest.'
}
[IO.Directory]::CreateDirectory($root) | Out-Null
$lease = [IO.File]::Open((Join-Path $root 'launcher.lease'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$guard = Start-AcceptancePowerRequest
$statusPath = Join-Path $root 'bootstrap-status.json'
$exchange = $plan.exchange
$lastCommand = '';
$deadline = [DateTime]::UtcNow.AddHours(4)
$script:phase = 'preparing';
$script:failure = $null;
$script:passed = $false;
$script:complete = $false
function Publish-Launcher
{
    Write-AcceptanceJson $statusPath @{formatVersion =1;
        updatedUtc                                   =[DateTime]::UtcNow.ToString('o');
        phase                                        =$script:phase;
        failure                                      =$script:failure;
        complete                                     =$script:complete;
        passed                                       =$script:passed;
        machine                                      =$env:COMPUTERNAME;
        taskId                                       =$plan.taskId
    }
    [IO.File]::Copy($statusPath, (Join-Path $exchange 'bootstrap-status.json'), $true)
}
function Wait-Command([string[]]$Allowed)
{
    $commandPath = Join-Path $exchange 'control.json'
    while ([DateTime]::UtcNow -lt $deadline)
    {
        if (Test-Path -LiteralPath $commandPath)
        {
            $command = Read-AcceptanceJson $commandPath
            if ($command.taskId -eq $plan.taskId -and $command.requestId -ne $script:lastCommand)
            {
                $script:lastCommand = $command.requestId
                if ($command.action -eq 'stop')
                {
                    throw 'Stopped by host; all evidence and incomplete fixtures are preserved.'
                }
                if ($command.action -in $Allowed)
                {
                    return $command
                }
            }
        }
        Start-Sleep -Seconds 3
    }
    throw 'Guest launcher deadline reached; incomplete evidence and fixtures are preserved.'
}
try
{
    $identityPath = Join-Path $root 'task-identity.json'
    $planHash = (Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash
    if (Test-Path -LiteralPath $identityPath)
    {
        $identity = Read-AcceptanceJson $identityPath;
        if ($identity.taskId -ne $plan.taskId -or $identity.planSha256 -ne $planHash)
        {
            throw 'An existing installer task has different inputs; preserve it.'
        }
    }
    else
    {
        Write-AcceptanceJson $identityPath @{taskId =$plan.taskId;
            planSha256                              =$planHash;
            createdUtc                              =[DateTime]::UtcNow.ToString('o')
        }
    }
    while ($true)
    {
        try
        {
            $statePath = Join-Path $root 'suite-state.json'
            $suiteDone = $false
            if (Test-Path -LiteralPath $statePath)
            {
                $state = Read-AcceptanceJson $statePath;
                $suiteDone = $state.complete -and $state.passed
            }
            if (!$suiteDone)
            {
                $script:phase = 'copying-verified-inputs';
                $script:failure = $null;
                Publish-Launcher
                $manifest = Read-AcceptanceJson (Join-Path $PSScriptRoot 'payload-manifest.json')
                foreach ($entry in $manifest.files)
                {
                    $script:phase = 'copying ' + $entry.target;
                    Publish-Launcher
                    $source = Join-Path $PSScriptRoot $entry.source
                    $target = Join-Path $root ('work/' + $entry.target)
                    Copy-ResumeEntry $source $target (Join-Path $root 'work') $entry.bytes $entry.sha256 | Out-Null
                }
                $localPlan = Join-Path $root 'work/plan.json'
                Copy-ResumeEntry $planPath $localPlan (Join-Path $root 'work') (Get-Item -LiteralPath $planPath).Length $planHash | Out-Null
                $script:phase = 'running-shipping-suite';
                Publish-Launcher
                $code = Invoke-AcceptanceProcess (Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe') @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'work/scripts/Test-ShippingInstaller.ps1'), '-PlanPath', $localPlan) (Join-Path $root 'suite-console') 12600 { Publish-Launcher }
                if ($code -ne 0)
                {
                    throw ('Shipping suite failed; exit ' + $code + '. Host may inspect the preserved logs and request an explicit retry.')
                }
            }
            $exportPath = Join-Path $root 'export-status.json'
            $zip = Join-Path $root 'installer-evidence.zip'
            if (!(Test-Path -LiteralPath $exportPath))
            {
                $script:phase = 'exporting-evidence';
                Publish-Launcher
                $probe = Join-Path $root 'work/probe/StudioX.ShippingInstallerValidation.exe'
                $code = Invoke-AcceptanceProcess $probe @('export', (Join-Path $root 'evidence'), $zip, $exportPath) (Join-Path $root 'export-console') 600
                if ($code -ne 0)
                {
                    throw 'Export failed; fixtures remain intact.'
                }
            }
            $export = Read-AcceptanceJson $exportPath
            $hostZip = Join-Path $exchange 'installer-evidence.zip'
            if (!(Test-Path -LiteralPath $hostZip))
            {
                [IO.File]::Copy($zip, $hostZip, $false)
            }
            if ((Get-Item -LiteralPath $hostZip).Length -ne $export.bytes -or (Get-FileHash -LiteralPath $hostZip -Algorithm SHA256).Hash -ne $export.sha256)
            {
                throw 'Host export fingerprint differs; fixtures are retained.'
            }
            [IO.File]::Copy($exportPath, (Join-Path $exchange 'export-status.json'), $true)
            [IO.File]::Copy((Join-Path $root 'suite-state.json'), (Join-Path $exchange 'suite-state.json'), $true)
            $script:phase = 'awaiting-host-verification';
            Publish-Launcher
            $command = Wait-Command @('cleanup', 'retry')
            if ($command.action -eq 'retry')
            {
                continue
            }
            if ($command.exportSha256 -ne $export.sha256)
            {
                throw 'Cleanup request does not match the exported evidence.'
            }
            $script:phase = 'cleaning-verified-fixtures';
            Publish-Launcher
            Complete-ShippingCleanup $root $exchange | Out-Null
            [IO.File]::Copy((Join-Path $root 'cleanup.json'), (Join-Path $exchange 'guest-cleanup.json'), $true)
            foreach ($file in Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -like 'cleanup-*' -or $_.Name -like '*console*' })
            {
                [IO.File]::Copy($file.FullName, (Join-Path $exchange $file.Name), $true)
            }
            $script:passed = $true;
            $script:complete = $true;
            $script:phase = 'complete';
            Publish-Launcher
            break
        }
        catch
        {
            $script:failure = $_.Exception.ToString() + [Environment]::NewLine + $_.ScriptStackTrace
            $script:phase = 'awaiting-retry';
            Publish-Launcher
            foreach ($file in Get-ChildItem -LiteralPath $root -File | Where-Object { $_.Name -like '*console*' -or $_.Name -eq 'suite-state.json' })
            {
                [IO.File]::Copy($file.FullName, (Join-Path $exchange $file.Name), $true)
            }
            Write-Warning $script:failure
            Write-Host 'Evidence retained. Waiting for a host retry or stop request; leave this window open.'
            Wait-Command @('retry') | Out-Null
        }
    }
}
finally
{
    Stop-AcceptancePowerRequest $guard;
    $lease.Dispose()
}
Write-Host 'Shipping installer acceptance and verified cleanup completed.'
