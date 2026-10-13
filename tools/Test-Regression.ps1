param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$SamplePlugin,
    [string]$VendorSvd,
    [string]$ToolsetsDirectory,
    [string]$CoreDumpFixtures,
    [string]$F407Pack,
    [string]$ProjectHealthNinja,
    [string]$EnvironmentReliabilityNinja,
    [string]$LanguageValidationMatrix,
    [string]$LanguageRuntime,
    [string]$PeripheralRuntime,
    [string]$PeripheralPackInputs,
    [string]$KeilValidationInputs,
    [string]$SourceRegistrationInputs,
    [ValidateSet('baseline', 'editor', 'keil', 'sources', 'environment', 'peripheral', 'fault')][string[]]$MaintenanceAreas = @('baseline')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Maintenance-Gates.ps1')
$maintenancePolicy = Read-StudioXMaintenancePolicy
$maintenanceAreasResolved = @(Get-StudioXMaintenanceAreas $maintenancePolicy $MaintenanceAreas)
$maintenanceInputs = @{}
foreach ($name in $maintenancePolicy.inputs.PSObject.Properties.Name)
{
    if ($PSBoundParameters.ContainsKey($name) -and ![string]::IsNullOrWhiteSpace($PSBoundParameters[$name]))
    {
        $maintenanceInputs[$name] = [IO.Path]::GetFullPath($PSBoundParameters[$name])
    }
}
# 先拒绝缺失的必跑输入，不能执行完基础回归才把真实验证悄悄视作未请求。
Assert-StudioXMaintenanceInputs $maintenancePolicy $maintenanceAreasResolved $maintenanceInputs
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Use a new regression output directory.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$build = Join-Path $output 'build'
$empty = Join-Path $output 'empty-runtime'
[IO.Directory]::CreateDirectory($empty) | Out-Null
$previous = $env:StudioXRuntimeAssetsDirectory
$results = [Collections.Generic.List[object]]::new()
$completed = $false
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw 'Unable to bind source commit.'
}
$sourceDirty = @(& git -C $root status --porcelain=v1).Count -gt 0
$sourceBefore = Get-StudioXMaintenanceSourceSnapshot $root
$inputFilesBefore = @(Get-StudioXMaintenanceInputFiles $maintenancePolicy $maintenanceInputs)
. (Join-Path $PSScriptRoot 'Regression-Evidence.ps1')
function Run-Check([string]$CheckName, [scriptblock]$Action)
{
    Invoke-StudioXRegressionCheck -CheckName $CheckName -Action $Action -EvidenceDirectory $output -Results $results
}
function Exe([string]$Name)
{
    Join-Path $build "bin/$Name/release_win-x64/$Name.exe"
}
try
{
    $env:StudioXRuntimeAssetsDirectory = $empty
    Run-Check 'build' { & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -BuildArtifactsDirectory $build }
    Run-Check 'security-boundaries' { & (Exe 'StudioX.SecurityValidation') (Join-Path $output 'security-boundaries') }
    Run-Check 'mon51-protocol' { & (Exe 'StudioX.Mon51Validation') (Join-Path $output 'mon51-protocol') }
    Run-Check 'source-style' { & (Join-Path $PSScriptRoot 'Format-Source.ps1') -Check -BuildArtifactsDirectory $build }
    Run-Check 'mcp-validation-build' { & dotnet build (Join-Path $PSScriptRoot 'StudioX.McpValidation/StudioX.McpValidation.csproj') -c Release --artifacts-path $build --nologo }
    Run-Check 'mcp-contracts-and-boundaries' { & (Exe 'StudioX.McpValidation') }
    if ($LanguageRuntime)
    {
        $languageRuntimePath = [IO.Path]::GetFullPath($LanguageRuntime)
        $editingOutput = Join-Path $output 'workspace-editing'
        Run-Check 'live-diagnostic-reliability' { & (Exe 'StudioX.WorkspaceEditingValidation') $languageRuntimePath $editingOutput }
        Run-Check 'live-diagnostic-ui' {
            $editingUi = Join-Path $output 'workspace-editing-ui'
            $editingDesktop = Join-Path $build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe'
            $editingProcess = Start-Process -FilePath $editingDesktop -ArgumentList @('--preview-workspace-editor', ('"' + $editingUi + '"'), ('"' + (Join-Path $editingOutput 'semantic-project') + '"'), ('"' + $languageRuntimePath + '"')) -WindowStyle Hidden -PassThru
            if (!$editingProcess.WaitForExit(60000))
            {
                $editingProcess.Kill($true);
                throw 'Workspace diagnostic UI timeout.'
            }
            if ($editingProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $editingUi 'result.txt')))
            {
                throw 'Workspace diagnostic UI failed.'
            }
            $global:LASTEXITCODE = 0
        }
    }
    if ($ProjectHealthNinja)
    {
        Run-Check 'project-health-build' { & dotnet build (Join-Path $PSScriptRoot 'StudioX.ProjectHealthValidation/StudioX.ProjectHealthValidation.csproj') -c Release --artifacts-path $build --nologo }
        Run-Check 'project-health-analysis' { & (Exe 'StudioX.ProjectHealthValidation') (Join-Path $output 'project-health') ([IO.Path]::GetFullPath($ProjectHealthNinja)) }
    }
    if ($EnvironmentReliabilityNinja)
    {
        Run-Check 'environment-recovery' { & (Exe 'StudioX.EnvironmentReliabilityValidation') --boundaries (Join-Path $output 'environment-recovery') ([IO.Path]::GetFullPath($EnvironmentReliabilityNinja)) }
    }
    if ($LanguageValidationMatrix)
    {
        $languageRows = @(Get-Content -LiteralPath $LanguageValidationMatrix -Raw | ConvertFrom-Json)
        if ($languageRows.Count -eq 0)
        {
            throw 'Language validation matrix must contain explicit existing runtime/project directories.'
        }
        Run-Check 'esp-language-build' { & dotnet build (Join-Path $PSScriptRoot 'StudioX.EspressifValidation/StudioX.EspressifValidation.csproj') -c Release --artifacts-path $build --nologo }
        for ($languageIndex = 0; $languageIndex -lt $languageRows.Count; $languageIndex++)
        {
            $languageRow = $languageRows[$languageIndex]
            if (!$languageRow.runtimeDirectory -or !$languageRow.projectsDirectory)
            {
                throw 'Each language row requires runtimeDirectory and projectsDirectory.'
            }
            $languageRuntime = [IO.Path]::GetFullPath($languageRow.runtimeDirectory)
            $languageProjects = [IO.Path]::GetFullPath($languageRow.projectsDirectory)
            $languageEvidence = Join-Path $output ('esp-language-' + $languageIndex)
            Run-Check ('esp-language-' + $languageIndex) { & (Exe 'StudioX.EspressifValidation') --language-diagnostics $languageRuntime $languageProjects $languageEvidence }
        }
    }
    foreach ($name in @('StudioX.DesktopArchitectureChecks', 'StudioX.ArchitectureChecks'))
    {
        Run-Check ($name + '-build') { & dotnet build (Join-Path $PSScriptRoot "$name/$name.csproj") -c Release --artifacts-path $build --nologo }
        Run-Check $name { & (Exe $name) }
    }
    Run-Check 'release-identity' { & (Join-Path $PSScriptRoot 'Test-ReleaseVersion.ps1') -OutputDirectory (Join-Path $output 'release-identity') }
    Run-Check 'release-pipeline-guards' { & (Join-Path $PSScriptRoot 'Test-ReleasePipeline.ps1') -OutputDirectory (Join-Path $output 'release-pipeline') }
    Run-Check 'maintenance-gate-contracts' { & (Join-Path $PSScriptRoot 'Test-MaintenanceGates.ps1') -OutputDirectory (Join-Path $output 'maintenance-gate-contracts') }
    Run-Check 'debug-plugin-isolation' { & (Exe 'StudioX.DebugPluginValidation') (Join-Path $build 'bin/StudioX.PluginHost/release_win-x64') (Join-Path $output 'debug-plugins') }
    Run-Check 'keil-migration-workflow' { & (Join-Path $PSScriptRoot 'Test-KeilWorkflow.ps1') -OutputDirectory (Join-Path $output 'keil-workflow') -BuildArtifactsDirectory $build -ValidationInputs $KeilValidationInputs }
    Run-Check 'project-file-synchronization' { & (Join-Path $PSScriptRoot 'Test-ProjectSynchronization.ps1') -OutputDirectory (Join-Path $output 'project-synchronization') -BuildArtifactsDirectory $build -LanguageRuntime $LanguageRuntime }
    Run-Check 'source-registration' { & (Join-Path $PSScriptRoot 'Test-SourceRegistration.ps1') -OutputDirectory (Join-Path $output 'source-registration') -BuildArtifactsDirectory $build -ValidationInputs $SourceRegistrationInputs }
    Run-Check 'openocd-plot-offline' { & (Exe 'StudioX.OpenOcdPlotChecks') }
    Run-Check 'code-templates' { & (Exe 'StudioX.CodeTemplateValidation') (Join-Path $output 'code-templates') }
    if ([bool]$PeripheralRuntime -ne [bool]$PeripheralPackInputs)
    {
        throw 'Peripheral validation requires both an existing runtime and pack inputs.'
    }
    $peripheralOutput = Join-Path $output 'peripheral-development'
    $peripheralArgs = @($peripheralOutput)
    if ($PeripheralRuntime)
    {
        $peripheralArgs += @([IO.Path]::GetFullPath($PeripheralRuntime), [IO.Path]::GetFullPath($PeripheralPackInputs))
    }
    Run-Check 'peripheral-development' { & (Exe 'StudioX.PeripheralDevelopmentValidation') @peripheralArgs }
    if ($PeripheralRuntime)
    {
        Run-Check 'peripheral-development-diagnostics' { & (Exe 'StudioX.PeripheralDevelopmentValidation') --diagnostics (Join-Path $output 'peripheral-development-diagnostics') ([IO.Path]::GetFullPath($PeripheralRuntime)) (Join-Path $peripheralOutput 'native-projects') }
        Run-Check 'peripheral-development-ui' {
            $peripheralUi = Join-Path $output 'peripheral-development-ui'
            $peripheralDesktop = Join-Path $build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe'
            $peripheralProcess = Start-Process -FilePath $peripheralDesktop -ArgumentList @('--preview-peripheral-development', ('"' + $peripheralUi + '"'), ('"' + (Join-Path $peripheralOutput 'native-projects/idf-0.1.1') + '"'), ('"' + [IO.Path]::GetFullPath($PeripheralRuntime) + '"')) -WindowStyle Hidden -PassThru
            if (!$peripheralProcess.WaitForExit(60000))
            {
                $peripheralProcess.Kill($true);
                throw 'Peripheral development UI timeout.'
            }
            if ($peripheralProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $peripheralUi 'result.json')))
            {
                throw 'Peripheral development UI failed.'
            }
            $peripheralResult = Get-Content -LiteralPath (Join-Path $peripheralUi 'result.json') -Raw | ConvertFrom-Json
            if (!$peripheralResult.success)
            {
                throw 'Peripheral development UI checks did not pass.'
            }
            $global:LASTEXITCODE = 0
        }
    }
    $faultArgs = @((Join-Path $output 'fault-peripherals'))
    if ($VendorSvd)
    {
        $faultArgs += ([IO.Path]::GetFullPath($VendorSvd))
    }
    if ($CoreDumpFixtures)
    {
        if (!$VendorSvd -or !$ToolsetsDirectory)
        {
            throw 'CoreDump validation requires explicit vendor SVD and existing managed tools.'
        }
        $faultArgs += @([IO.Path]::GetFullPath($ToolsetsDirectory), [IO.Path]::GetFullPath($CoreDumpFixtures))
    }
    Run-Check 'fault-peripherals' { & (Exe 'StudioX.FaultPeripheralValidation') @faultArgs }
    if (!$SamplePlugin)
    {
        Run-Check 'sample-plugin' { & (Join-Path $PSScriptRoot 'Build-PluginSample.ps1') -OutputDirectory (Join-Path $output 'plugin') -BuildArtifactsDirectory $build -Development }
        $SamplePlugin = Join-Path $output 'plugin/studiox.development-1.0.0.studioxplugin'
    }
    $workflowArgs = @((Join-Path $output 'product-workflows'), [IO.Path]::GetFullPath($SamplePlugin))
    if ($F407Pack)
    {
        if (!$ToolsetsDirectory)
        {
            throw 'Native F407 build validation requires explicit managed tools.'
        }
        $workflowArgs += @([IO.Path]::GetFullPath($ToolsetsDirectory), [IO.Path]::GetFullPath($F407Pack))
    }
    Run-Check 'product-workflows' { & (Exe 'StudioX.ProductWorkflowValidation') @workflowArgs }
    $desktop = Join-Path $build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe'
    Run-Check 'pack-retention-build' { & dotnet build (Join-Path $PSScriptRoot 'StudioX.PackRetentionValidation/StudioX.PackRetentionValidation.csproj') -c Release --artifacts-path $build --nologo }
    $packRetention = Join-Path $output 'pack-retention'
    Run-Check 'pack-retention' { & (Exe 'StudioX.PackRetentionValidation') $packRetention }
    foreach ($packUiKind in @('pack-catalog', 'editable-combo'))
    {
        Run-Check ($packUiKind + '-ui') {
            $packUi = Join-Path $output ($packUiKind + '-ui')
            $packUiArguments = if ($packUiKind -eq 'pack-catalog')
            {
                @('--preview-pack-catalog', ('"' + $packUi + '"'),
                    ('"' + (Join-Path $packRetention 'prune-project/archives/fixture.same-0.9.0.mcupack') + '"'),
                    ('"' + (Join-Path $packRetention 'prune-project/archives/fixture.same-0.10.0.mcupack') + '"'))
            }
            else { @('--preview-editable-combos', ('"' + $packUi + '"')) }
            $packUiProcess = Start-Process -FilePath $desktop -ArgumentList $packUiArguments -WindowStyle Hidden -PassThru
            if (!$packUiProcess.WaitForExit(45000))
            {
                $packUiProcess.Kill($true)
                throw ($packUiKind + ' UI timeout.')
            }
            $packUiResult = Join-Path $packUi $(if ($packUiKind -eq 'pack-catalog') { 'loading-result.json' } else { 'result.json' })
            if ($packUiProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath $packUiResult) -or
                !(Get-Content -LiteralPath $packUiResult -Raw | ConvertFrom-Json).success)
            {
                throw ($packUiKind + ' UI failed.')
            }
            $global:LASTEXITCODE = 0
        }
    }
    Run-Check 'code-template-ui' {
        $templateUi = Join-Path $output 'code-template-ui'
        $templateProcess = Start-Process -FilePath $desktop -ArgumentList @('--preview-code-templates', ('"' + $templateUi + '"')) -WindowStyle Hidden -PassThru
        if (!$templateProcess.WaitForExit(45000))
        {
            $templateProcess.Kill($true);
            throw 'Code template UI preview timeout.'
        }
        if ($templateProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $templateUi 'result.json')))
        {
            throw 'Code template UI preview failed.'
        }
        $templateResult = Get-Content -LiteralPath (Join-Path $templateUi 'result.json') -Raw | ConvertFrom-Json
        if (!$templateResult.success)
        {
            throw 'Code template UI checks did not pass.'
        }
        $global:LASTEXITCODE = 0
    }
    $svd = Join-Path $output 'fault-peripherals/fixture.svd'
    Run-Check 'fault-peripheral-ui' {
        $ui = Join-Path $output 'ui';
        [IO.Directory]::CreateDirectory($ui) | Out-Null
        $process = Start-Process -FilePath $desktop -ArgumentList @('--preview-fault-peripherals', ('"' + $ui + '"'), ('"' + $svd + '"')) -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(120000))
        {
            $process.Kill($true);
            throw 'UI preview timeout.'
        }
        if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $ui 'result.json')))
        {
            throw 'UI preview failed.'
        }
        $global:LASTEXITCODE = 0
    }
    Run-Check 'mon51-ui' {
        $mon51Ui = Join-Path $output 'mon51-ui'
        $mon51Process = Start-Process -FilePath $desktop -ArgumentList @('--preview-mon51', ('"' + $mon51Ui + '"')) -WindowStyle Hidden -PassThru
        if (!$mon51Process.WaitForExit(60000))
        {
            $mon51Process.Kill($true)
            throw 'Mon51 UI preview timeout.'
        }
        if ($mon51Process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $mon51Ui 'result.txt')))
        {
            throw 'Mon51 UI preview failed.'
        }
        $global:LASTEXITCODE = 0
    }
    $completed = $true
}
finally
{
    $env:StudioXRuntimeAssetsDirectory = $previous
    $sourceAfter = $null
    $inputFilesAfter = @()
    $headAfter = $null
    $snapshotDiagnostic = $null
    try
    {
        $sourceAfter = Get-StudioXMaintenanceSourceSnapshot $root
        $inputFilesAfter = @(Get-StudioXMaintenanceInputFiles $maintenancePolicy $maintenanceInputs)
        $headAfter = (& git -C $root rev-parse HEAD).Trim()
        if ($LASTEXITCODE -ne 0) { throw 'Final source commit unavailable.' }
    }
    catch { $snapshotDiagnostic = $_.Exception.ToString() }
    $report = @{formatVersion   =1;
        sourceCommit  =$head;
        sourceDirty   =$sourceDirty;
        hardware      =$false;
        downloadedSdk =$false;
        passed        =($completed -and $results.Count -gt 0 -and !($results | Where-Object { !$_.passed }));
        results       =$results;
        maintenanceAreas = @($maintenanceAreasResolved);
        maintenanceInputs = $maintenanceInputs;
        sourceSnapshot = @{before=$sourceBefore; after=$sourceAfter; diagnostic=$snapshotDiagnostic}
        inputFileSnapshot = @{before=$inputFilesBefore; after=$inputFilesAfter}
    }
    $report.maintenance = Test-StudioXMaintenanceEvidence $maintenancePolicy $maintenanceAreasResolved $report $output $sourceAfter $headAfter
    $report.passed = $report.passed -and $report.maintenance.passed
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'regression.json') -Encoding utf8
    if ($completed -and !$report.maintenance.passed)
    {
        throw ('Maintenance gates failed; evidence retained in regression.json: ' + ($report.maintenance.errors -join '; '))
    }
}
