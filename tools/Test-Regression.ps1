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
    [string]$PeripheralPackInputs
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
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
$env:StudioXRuntimeAssetsDirectory = $empty
$results = [Collections.Generic.List[object]]::new()
$completed = $false
$head = (& git -C $root rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0)
{
    throw 'Unable to bind source commit.'
}
$sourceDirty = @(& git -C $root status --porcelain=v1).Count -gt 0
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
    Run-Check 'build' { & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -BuildArtifactsDirectory $build }
    Run-Check 'security-boundaries' { & (Exe 'StudioX.SecurityValidation') (Join-Path $output 'security-boundaries') }
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
    Run-Check 'debug-plugin-isolation' { & (Exe 'StudioX.DebugPluginValidation') (Join-Path $build 'bin/StudioX.PluginHost/release_win-x64') (Join-Path $output 'debug-plugins') }
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
    $completed = $true
}
finally
{
    $env:StudioXRuntimeAssetsDirectory = $previous
    @{formatVersion   =1;
        sourceCommit  =$head;
        sourceDirty   =$sourceDirty;
        hardware      =$false;
        downloadedSdk =$false;
        passed        =($completed -and $results.Count -gt 0 -and !($results | Where-Object { !$_.passed }));
        results       =$results
    } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'regression.json') -Encoding utf8
}
