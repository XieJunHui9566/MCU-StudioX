param(
    [Parameter(Mandatory = $true)][string]$PlanPath,
    [Parameter(Mandatory = $true)][string]$ExchangeDirectory,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
. (Join-Path $PSScriptRoot 'ShippingInstaller-Common.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$plan = Read-AcceptanceJson $PlanPath
$export = Read-AcceptanceJson (Join-Path $ExchangeDirectory 'export-status.json')
$state = Read-AcceptanceJson (Join-Path $ExchangeDirectory 'suite-state.json')
if (!$state.complete -or !$state.passed -or !$export.passed)
{
    throw 'The complete guest suite and export must both pass before host verification.'
}
$archive = Join-Path $ExchangeDirectory 'installer-evidence.zip'
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ((Get-Item -LiteralPath $archive).Length -ne $export.bytes -or $hash -ne $export.sha256)
{
    throw 'Host export length or SHA-256 differs from the guest receipt.'
}
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try
{
    # 独立读取 ZIP 流而不解压；拒绝重名和越界路径，逐项核对原始证据。
    $entries = @{}
    foreach ($entry in $zip.Entries)
    {
        $name = $entry.FullName
        if ($name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or @($name.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count)
        {
            throw ('Unsafe evidence entry: ' + $name)
        }
        if ($entries.ContainsKey($name))
        {
            throw ('Duplicate evidence entry: ' + $name)
        }
        if ($entry.Length -gt 256MB)
        {
            throw ('Evidence entry exceeds bounds: ' + $name)
        }
        $entries[$name] = $entry
    }
    function Read-ZipJson([string]$Name)
    {
        if (!$entries.ContainsKey($Name))
        {
            throw ('Required evidence is absent: ' + $Name)
        }
        $reader = [IO.StreamReader]::new($entries[$Name].Open(), [Text.Encoding]::UTF8)
        try
        {
            $reader.ReadToEnd() | ConvertFrom-Json
        }
        finally
        {
            $reader.Dispose()
        }
    }
    $manifest = Read-ZipJson 'evidence-manifest.json'
    if ($manifest.formatVersion -ne 1 -or $manifest.files.Count -ne $export.fileCount -or $entries.Count -ne $manifest.files.Count + 1)
    {
        throw 'Evidence manifest membership differs from the export.'
    }
    $seen = @{};
    $total = 0L
    foreach ($file in $manifest.files)
    {
        if ($file.path -eq 'evidence-manifest.json' -or $seen.ContainsKey($file.path) -or !$entries.ContainsKey($file.path))
        {
            throw 'Evidence manifest contains a repeated or missing file.'
        }
        $seen[$file.path] = $true
        $entry = $entries[$file.path]
        if ($entry.Length -ne $file.bytes)
        {
            throw ('Evidence length differs: ' + $file.path)
        }
        $total += $entry.Length
        if ($total -gt 4GB)
        {
            throw 'Uncompressed evidence exceeds declared bounds.'
        }
        $stream = $entry.Open();
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try
        {
            $actual = [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '')
        }
        finally
        {
            $algorithm.Dispose();
            $stream.Dispose()
        }
        if ($actual -ne $file.sha256)
        {
            throw ('Evidence SHA-256 differs: ' + $file.path)
        }
    }
    $result = Read-ZipJson 'result.json'
    if (!$result.passed -or !$result.actualShippingInstallers -or !$result.cleanWindowsGuest -or $result.hardware -or $result.downloadedSdk)
    {
        throw 'Guest result does not match the requested software-only shipping acceptance.'
    }
    if ($result.plan.taskId -ne $plan.taskId -or $result.plan.guestRoot -ne $plan.guestRoot -or $result.version -ne $plan.version -or $result.previousVersion -ne $plan.previousVersion)
    {
        throw 'Guest result belongs to another task or version.'
    }
    foreach ($input in @('light', 'full', 'previous', 'arm'))
    {
        foreach ($field in @('file', 'bytes', 'sha256'))
        {
            if ($result.plan.$input.$field -ne $plan.$input.$field)
            {
                throw ('Guest shipping input differs: ' + $input + '/' + $field)
            }
        }
    }
    $stages = @('01-fresh-light', '02-create-missing-component-project', '02-import-selected-component', '02-build-light-project', '03-first-project-ui', '04-light-to-full', '05-full-esp-project', '06-installation-guards', '07-full-to-light-and-repair', '08-uninstall-light-preserves-data', '09-actual-previous-version', '10-actual-upgrade-to-current', '11-uninstall-upgraded-full', '12-final-environment')
    if ($result.completed.Count -ne $stages.Count -or $state.completed.Count -ne $stages.Count)
    {
        throw 'Guest completed-stage count differs from this acceptance plan.'
    }
    foreach ($stage in $stages)
    {
        if ($stage -notin $result.completed -or $stage -notin $state.completed)
        {
            throw ('Required stage is incomplete: ' + $stage)
        }
    }
    $payloads = @(foreach ($name in @('fresh-light-payload', 'full-payload', 'repaired-light-payload', 'upgraded-program-payload'))
        {
            $item = Read-ZipJson ($name + '.json')
            if (!$item.passed -or $item.verifiedFiles -le 0)
            {
                throw ('Payload evidence failed: ' + $name)
            }
            @{name = $name; verifiedFiles = $item.verifiedFiles; preservedFiles = $item.preservedFiles; manifestSha256 = $item.manifestSha256 }
        })
    $serviceReports = @('arm-created', 'arm-imported', 'arm-built-light', 'esp-created', 'esp-built-full', 'old-project-created', 'old-project-built', 'old-project-rebuilt-current')
    foreach ($name in $serviceReports)
    {
        $item = Read-ZipJson ($name + '.json')
        if (!$item.passed -or !$item.bindings.passed -or !$item.bindings.actualShippingAssemblies -or $item.bindings.files.Count -lt 4)
        {
            throw ('Installed-service evidence failed: ' + $name)
        }
        if ($item.bindings.installed -notin @(($plan.guestRoot + '\work\Installed Light'), ($plan.guestRoot + '\work\Upgraded Full')))
        {
            throw ('Service evidence used an unexpected installation: ' + $name)
        }
        foreach ($binding in $item.bindings.files)
        {
            if ($binding.sha256 -notmatch '^[0-9A-Fa-f]{64}$' -or $binding.path -notmatch '^StudioX\.[^\\/]+\.dll$')
            {
                throw 'Invalid installed assembly evidence.'
            }
        }
        if ($name -in @('arm-built-light', 'esp-built-full', 'old-project-built', 'old-project-rebuilt-current'))
        {
            if (!$item.result.success -or !$entries.ContainsKey($name + '.json.build.log'))
            {
                throw ('Actual build evidence is incomplete: ' + $name)
            }
        }
    }
    $preserved = @('arm-preserved-by-full', 'projects-after-switch', 'projects-after-uninstall', 'old-project-after-upgrade')
    foreach ($prefix in @('a', 'b'))
    {
        foreach ($tree in @('toolsets', 'hdl', 'stc-isp'))
        {
            foreach ($operation in @($(if ($prefix -eq 'a')
                        {
                            'switch'
                        }
                        else
                        {
                            'upgrade'
                        }), 'uninstall'))
            {
                $preserved += ($prefix + '-' + $tree + '-after-' + $operation)
            }
        }
    }
    foreach ($name in $preserved)
    {
        $item = Read-ZipJson ($name + '.json');
        if (!$item.passed -or $item.changed.Count -ne 0)
        {
            throw ('Preservation evidence failed: ' + $name)
        }
    }
    $ui = Read-ZipJson 'first-project-ui/result.json'
    if ($ui.status -ne 'passed')
    {
        throw 'Actual first-project WPF workflow failed.'
    }
    $before = Read-ZipJson 'baseline-before.json';
    $after = Read-ZipJson 'baseline-after.json'
    if ($before.machine -ne $plan.guestMachine -or $before.productInstalled -or $after.productInstalled -or !$after.persistentEnvironmentUnchanged)
    {
        throw 'Guest baseline does not satisfy the installation acceptance.'
    }
    foreach ($scope in @('User', 'Machine'))
    {
        foreach ($property in $before.environment.$scope.PSObject.Properties)
        {
            if ($property.Value -ne $after.environment.$scope.($property.Name))
            {
                throw ('Persistent environment differs: ' + $scope + '/' + $property.Name)
            }
        }
    }
    Write-AcceptanceJson $OutputPath @{formatVersion =1;
        passed                                       =$true;
        taskId                                       =$plan.taskId;
        verifiedUtc                                  =[DateTime]::UtcNow.ToString('o');
        archiveSha256                                =$hash;
        archiveBytes                                 =$export.bytes;
        verifiedEvidenceFiles                        =$manifest.files.Count;
        uncompressedEvidenceBytes                    =$total;
        completedStages                              =$stages.Count;
        checks                                       =$result.checks.Count;
        payloads                                     =$payloads;
        installedServiceReports                      =$serviceReports.Count;
        preservedTreeReports                         =$preserved.Count;
        firstProjectUiPassed                         =$true;
        guestBaseline                                =$before;
        version                                      =$plan.version;
        previousVersion                              =$plan.previousVersion;
        sourceCommit                                 =$plan.sourceCommit;
        hardware                                     =$false;
        downloadedSdk                                =$false
    }
}
finally
{
    $zip.Dispose()
}
Write-Host 'Host independently verified every evidence entry and required shipping workflow.'
