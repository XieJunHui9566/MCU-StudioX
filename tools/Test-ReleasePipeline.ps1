param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use new evidence directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$props=[xml](Get-Content (Join-Path $root 'Directory.Build.props') -Raw)
$version=$props.SelectSingleNode('//ProductVersion').InnerText
$before=(Get-FileHash (Join-Path $root 'Directory.Build.props')).Hash
$checks=[Collections.Generic.List[string]]::new()
. (Join-Path $PSScriptRoot 'Release-Evidence.ps1')
. (Join-Path $PSScriptRoot 'Regression-Evidence.ps1')
$failedResults=[Collections.Generic.List[object]]::new()
$currentShell=(Get-Process -Id $PID).Path
$rejected=$false
try { Invoke-StudioXRegressionCheck -CheckName 'intentional-native-failure' -Action { & $currentShell -NoProfile -Command 'exit 7' } -EvidenceDirectory $output -Results $failedResults }
catch { $rejected=$_.Exception.Message -match 'intentional-native-failure failed' }
if(!$rejected -or $failedResults.Count -ne 1 -or $failedResults[0].passed -or $failedResults[0].diagnostic -notmatch 'exited 7'){throw 'Native failure incorrectly reported as passing.'}
$checks.Add('native nonzero exit stops regression and records failed evidence')
$sampleA=Join-Path $output 'checksum-a.txt';$sampleB=Join-Path $output 'checksum-b.txt'
'first'|Set-Content $sampleA -Encoding ascii;'second'|Set-Content $sampleB -Encoding ascii
$sum=Join-Path $output 'SHA256SUMS.txt';Write-StudioXReleaseChecksums @($sampleA,$sampleB) $sum
$lines=@(Get-Content $sum)
if($lines.Count -ne 2 -or $lines[0] -notmatch '^[0-9a-f]{64}  checksum-a.txt$' -or $lines[1] -notmatch '^[0-9a-f]{64}  checksum-b.txt$'){throw 'Checksums concatenated or invalid.'}
$checks.Add('installer/source checksum entries remain separate and correctly formatted')
& (Join-Path $PSScriptRoot 'Invoke-Release.ps1') -ReleaseVersion $version -OutputDirectory (Join-Path $output 'plan')
$plan=Get-Content (Join-Path $output 'plan/plan.json') -Raw|ConvertFrom-Json
if($plan.stage -ne 'Plan' -or $plan.version -ne $version -or $plan.releaseTag -ne ('v'+$version) -or $plan.automaticVersionChange -or $plan.profile -ne 'light'){throw 'Unsafe release defaults.'}
$checks.Add('default stage only writes reviewable plan')
foreach($profile in @('full','light','base'))
{
    & (Join-Path $PSScriptRoot 'Invoke-Release.ps1') -ReleaseVersion $version -DistributionProfile $profile -OutputDirectory (Join-Path $output ('profile-'+$profile))
    $profilePlan=Get-Content (Join-Path $output ('profile-'+$profile+'/plan.json')) -Raw|ConvertFrom-Json
    $expectedProfile=if($profile -eq 'base'){'light'}else{$profile}
    if($profilePlan.profile -ne $expectedProfile -or $profilePlan.version -ne $version){throw 'Distribution profile changed product identity or was not canonicalized.'}
}
$checks.Add('full and light plans retain identity; legacy base resolves to light')
& (Join-Path $PSScriptRoot 'Invoke-Release.ps1') -ReleaseVersion $version -ReleaseTag ('v'+$version+'-rebuild-20261003') -OutputDirectory (Join-Path $output 'rebuild-plan')
$rebuildPlan=Get-Content (Join-Path $output 'rebuild-plan/plan.json') -Raw|ConvertFrom-Json
if($rebuildPlan.version -ne $version -or $rebuildPlan.releaseTag -ne ('v'+$version+'-rebuild-20261003') -or $rebuildPlan.automaticVersionChange){throw 'Rebuild changed product identity.'}
$checks.Add('same-version rebuild selects a new tag without changing product identity')
foreach($invalidTag in @('v9.9.9-rebuild','--upload','v'+$version+'-../old'))
{
    $rejected=$false
    try{& (Join-Path $PSScriptRoot 'Invoke-Release.ps1') -ReleaseVersion $version -ReleaseTag $invalidTag -OutputDirectory (Join-Path $output ([Guid]::NewGuid().ToString('N')))}catch{$rejected=$_.Exception.Message -match 'Release tag must match'}
    if(!$rejected){throw 'Invalid or mismatched rebuild tag was accepted.'}
}
$checks.Add('invalid and mismatched release tags are rejected before publication')
$rejected=$false
try{& (Join-Path $PSScriptRoot 'Invoke-Release.ps1') -ReleaseVersion '1.2.3.4' -OutputDirectory (Join-Path $output 'wrong-version')}catch{$rejected=$_.Exception.Message -match 'version differs'}
if(!$rejected){throw 'Version mismatch not rejected.'}
$checks.Add('version mismatch rejected without editing product identity')
$rejected=$false
try{& (Join-Path $PSScriptRoot 'Invoke-Release.ps1') -ReleaseVersion $version -OutputDirectory (Join-Path $output 'publish-rejected') -Stage Publish}catch{$rejected=$_.Exception.Message -match 'clean checkout|regression report|explicit authorization'}
if(!$rejected){throw 'Unapproved or unverified publish reached network stage.'}
$checks.Add('missing clean source or evidence prevents publish before network operations')
if((Get-FileHash (Join-Path $root 'Directory.Build.props')).Hash -ne $before){throw 'Version file changed.'}
if(Get-ChildItem $output -Recurse -File | Where-Object {$_.Extension -in @('.exe','.zip')}){throw 'Plan generated unexpected release artifacts.'}
$checks.Add('plan and rejected publish never build installer or source archive')
@{success=$true;checks=$checks}|ConvertTo-Json -Depth 4|Set-Content (Join-Path $output 'result.json') -Encoding utf8
$global:LASTEXITCODE=0
