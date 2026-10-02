param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [string]$SamplePlugin,
    [string]$VendorSvd,
    [string]$ToolsetsDirectory,
    [string]$CoreDumpFixtures,
    [string]$F407Pack
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new regression output directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$build=Join-Path $output 'build'
$empty=Join-Path $output 'empty-runtime'
[IO.Directory]::CreateDirectory($empty)|Out-Null
$previous=$env:StudioXRuntimeAssetsDirectory
$env:StudioXRuntimeAssetsDirectory=$empty
$results=[Collections.Generic.List[object]]::new()
$completed=$false
$head=(& git -C $root rev-parse HEAD).Trim()
if($LASTEXITCODE -ne 0){throw 'Unable to bind source commit.'}
$sourceDirty=@(& git -C $root status --porcelain=v1).Count -gt 0
. (Join-Path $PSScriptRoot 'Regression-Evidence.ps1')
function Run-Check([string]$CheckName,[scriptblock]$Action)
{
    Invoke-StudioXRegressionCheck -CheckName $CheckName -Action $Action -EvidenceDirectory $output -Results $results
}
function Exe([string]$Name){Join-Path $build "bin/$Name/release_win-x64/$Name.exe"}
try
{
    Run-Check 'build' { & (Join-Path $PSScriptRoot 'Build.ps1') -Configuration Release -BuildArtifactsDirectory $build }
    foreach($name in @('StudioX.DesktopArchitectureChecks','StudioX.ArchitectureChecks'))
    {
        Run-Check ($name+'-build') { & dotnet build (Join-Path $PSScriptRoot "$name/$name.csproj") -c Release --artifacts-path $build --nologo }
        Run-Check $name { & (Exe $name) }
    }
    Run-Check 'release-identity' { & (Join-Path $PSScriptRoot 'Test-ReleaseVersion.ps1') -OutputDirectory (Join-Path $output 'release-identity') }
    Run-Check 'release-pipeline-guards' { & (Join-Path $PSScriptRoot 'Test-ReleasePipeline.ps1') -OutputDirectory (Join-Path $output 'release-pipeline') }
    Run-Check 'debug-plugin-isolation' { & (Exe 'StudioX.DebugPluginValidation') (Join-Path $build 'bin/StudioX.PluginHost/release_win-x64') (Join-Path $output 'debug-plugins') }
    Run-Check 'openocd-plot-offline' { & (Exe 'StudioX.OpenOcdPlotChecks') }
    Run-Check 'code-templates' { & (Exe 'StudioX.CodeTemplateValidation') (Join-Path $output 'code-templates') }
    $faultArgs=@((Join-Path $output 'fault-peripherals'))
    if($VendorSvd){$faultArgs+=([IO.Path]::GetFullPath($VendorSvd))}
    if($CoreDumpFixtures)
    {
        if(!$VendorSvd -or !$ToolsetsDirectory){throw 'CoreDump validation requires explicit vendor SVD and existing managed tools.'}
        $faultArgs+=@([IO.Path]::GetFullPath($ToolsetsDirectory),[IO.Path]::GetFullPath($CoreDumpFixtures))
    }
    Run-Check 'fault-peripherals' { & (Exe 'StudioX.FaultPeripheralValidation') @faultArgs }
    if(!$SamplePlugin)
    {
        Run-Check 'sample-plugin' { & (Join-Path $PSScriptRoot 'Build-PluginSample.ps1') -OutputDirectory (Join-Path $output 'plugin') -BuildArtifactsDirectory $build -Development }
        $SamplePlugin=Join-Path $output 'plugin/studiox.development-1.0.0.studioxplugin'
    }
    $workflowArgs=@((Join-Path $output 'product-workflows'),[IO.Path]::GetFullPath($SamplePlugin))
    if($F407Pack)
    {
        if(!$ToolsetsDirectory){throw 'Native F407 build validation requires explicit managed tools.'}
        $workflowArgs+=@([IO.Path]::GetFullPath($ToolsetsDirectory),[IO.Path]::GetFullPath($F407Pack))
    }
    Run-Check 'product-workflows' { & (Exe 'StudioX.ProductWorkflowValidation') @workflowArgs }
    $desktop=Join-Path $build 'bin/StudioX.Desktop/release_win-x64/MCU StudioX.exe'
    Run-Check 'code-template-ui' {
        $templateUi=Join-Path $output 'code-template-ui'
        $templateProcess=Start-Process -FilePath $desktop -ArgumentList @('--preview-code-templates',('"'+$templateUi+'"')) -WindowStyle Hidden -PassThru
        if(!$templateProcess.WaitForExit(45000)){ $templateProcess.Kill($true); throw 'Code template UI preview timeout.' }
        if($templateProcess.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $templateUi 'result.json'))){throw 'Code template UI preview failed.'}
        $templateResult=Get-Content -LiteralPath (Join-Path $templateUi 'result.json') -Raw|ConvertFrom-Json
        if(!$templateResult.success){throw 'Code template UI checks did not pass.'}
        $global:LASTEXITCODE=0
    }
    $svd=Join-Path $output 'fault-peripherals/fixture.svd'
    Run-Check 'fault-peripheral-ui' {
        $ui=Join-Path $output 'ui'; [IO.Directory]::CreateDirectory($ui)|Out-Null
        $process=Start-Process -FilePath $desktop -ArgumentList @('--preview-fault-peripherals',('"'+$ui+'"'),('"'+$svd+'"')) -WindowStyle Hidden -PassThru
        if(!$process.WaitForExit(120000)){ $process.Kill($true); throw 'UI preview timeout.' }
        if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath (Join-Path $ui 'result.json'))){throw 'UI preview failed.'}
        $global:LASTEXITCODE=0
    }
    $completed=$true
}
finally
{
    $env:StudioXRuntimeAssetsDirectory=$previous
    @{formatVersion=1;sourceCommit=$head;sourceDirty=$sourceDirty;hardware=$false;downloadedSdk=$false;passed=($completed -and $results.Count -gt 0 -and !($results|Where-Object {!$_.passed}));results=$results} |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'regression.json') -Encoding utf8
}
