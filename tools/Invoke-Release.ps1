param(
    [Parameter(Mandatory=$true)][string]$ReleaseVersion,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [ValidateSet('Plan','Verify','Package','Publish')][string]$Stage='Plan',
    [ValidateSet('base','light','full')][string]$DistributionProfile='light',
    [string]$PayloadDirectory,
    [string]$DevicePackCatalogDirectory,
    [string]$CompilerPath,
    [string]$Repository,
    [string]$ReleaseNotes,
    [string]$Installer,
    [string]$RegressionReport,
    [string]$ArtifactRecord,
    [string]$ReleaseTag,
    [switch]$AuthorizePublish
)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
. (Join-Path $PSScriptRoot 'Distribution-Profile.ps1')
$DistributionProfile = Resolve-StudioXDistributionProfile $DistributionProfile
. (Join-Path $PSScriptRoot 'Release-Evidence.ps1')
$identity=Get-StudioXReleaseVersion $ReleaseVersion
$props=[xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
if($props.SelectSingleNode('//ProductVersion').InnerText -ne $ReleaseVersion){throw 'Requested version differs from source. This script never changes product version.'}
if(!$ReleaseTag){$ReleaseTag='v'+$ReleaseVersion}
# 同版本重打包使用新标签，不能将既有发布标签移到另一个提交。
if($ReleaseTag -ne ('v'+$ReleaseVersion) -and $ReleaseTag -notmatch ('^v'+[regex]::Escape($ReleaseVersion)+'-[A-Za-z0-9][A-Za-z0-9_-]*$')){throw 'Release tag must match the product version, optionally followed by a rebuild suffix.'}
$installerName=Get-StudioXInstallerName $ReleaseVersion $DistributionProfile
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a new release evidence directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
Push-Location $root
try
{
    $head=(& git rev-parse HEAD).Trim(); if($LASTEXITCODE -ne 0){throw 'Git identity unavailable.'}
    $dirty=@(& git status --porcelain=v1)
    $plan=@{formatVersion=1;version=$ReleaseVersion;releaseTag=$ReleaseTag;fileVersion=$identity.FileVersion;stage=$Stage;profile=$DistributionProfile;sourceCommit=$head;dirtyPaths=$dirty.Count;
        actions=@('compile and run offline regression','verify exact installer identity and hashes','create draft GitHub release only on explicit Publish stage');automaticVersionChange=$false}
    $plan|ConvertTo-Json -Depth 6|Set-Content (Join-Path $output 'plan.json') -Encoding utf8
    if($Stage -eq 'Plan'){Write-Output (Join-Path $output 'plan.json'); return}
    if($Stage -eq 'Verify') { & (Join-Path $PSScriptRoot 'Test-Regression.ps1') -OutputDirectory (Join-Path $output 'regression'); return }
    if($dirty.Count){throw 'Packaging or publishing requires reviewed, committed source in a clean checkout.'}
    if(!$RegressionReport){throw 'A passing regression report is required.'}
    $reg=Get-Content -LiteralPath $RegressionReport -Raw|ConvertFrom-Json
    if($reg.formatVersion -ne 1 -or !$reg.passed -or $reg.results.Count -lt 8 -or @($reg.results|Where-Object {!$_.passed}).Count){throw 'Regression evidence is incomplete or failed.'}
    # 验证记录必须绑定本次提交，避免用其他源码的旧通过记录发行。
    if($reg.sourceCommit -ne $head -or $reg.sourceDirty -ne $false){throw 'Regression evidence must bind this exact clean source commit.'}
    if($Stage -eq 'Package')
    {
        if(!$PayloadDirectory -or !$CompilerPath){throw 'Provide prepared payload and pinned local installer compiler. No SDK/compiler is auto-downloaded.'}
        $payloadRecord=Get-Content -LiteralPath (Join-Path $PayloadDirectory 'release.json') -Raw|ConvertFrom-Json
        if($payloadRecord.sourceCommit -ne $head -or $payloadRecord.sourceDirty -ne $false){throw 'Payload must originate from this exact clean source commit.'}
        & (Join-Path $PSScriptRoot 'Build-Installer.ps1') -ReleaseVersion $ReleaseVersion -PayloadDirectory $PayloadDirectory -OutputDirectory (Join-Path $output 'installer') -DistributionProfile $DistributionProfile -DevicePackCatalogDirectory $DevicePackCatalogDirectory -CompilerPath $CompilerPath
        if($LASTEXITCODE -ne 0){throw 'Installer build failed.'}
        $packaged=Get-Item -LiteralPath (Join-Path $output ('installer/'+$installerName))
        @{formatVersion=1;sourceCommit=$head;version=$ReleaseVersion;profile=$DistributionProfile;installer=$packaged.Name;bytes=$packaged.Length;sha256=(Get-FileHash -LiteralPath $packaged.FullName).Hash;
            regressionSha256=(Get-FileHash -LiteralPath $RegressionReport).Hash}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $output 'artifact-record.json') -Encoding utf8
        return
    }
    if(!$AuthorizePublish -or $Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or !$ReleaseNotes -or !$Installer -or !$ArtifactRecord){throw 'Publish requires explicit authorization switch, repository, installer, package evidence and reviewable release notes.'}
    $file=Get-Item -LiteralPath $Installer
    if($file.Name -ne $installerName){throw 'Installer filename/version mismatch.'}
    if($file.VersionInfo.FileVersion.Trim() -ne $identity.FileVersion -or $file.VersionInfo.ProductVersion.Trim() -ne $ReleaseVersion){throw 'Installer embedded version mismatch.'}
    $record=Get-Content -LiteralPath $ArtifactRecord -Raw|ConvertFrom-Json
    $installerHash=(Get-FileHash -LiteralPath $file.FullName).Hash
    if($record.formatVersion -ne 1 -or $record.sourceCommit -ne $head -or $record.version -ne $ReleaseVersion -or $record.profile -ne $DistributionProfile -or $record.installer -ne $file.Name -or $record.bytes -ne $file.Length -or $record.sha256 -ne $installerHash -or $record.regressionSha256 -ne (Get-FileHash -LiteralPath $RegressionReport).Hash){throw 'Package evidence does not bind installer and regression to current source.'}
    $sourceArchive=Join-Path $output "MCU-StudioX-$ReleaseVersion-source.zip"
    & git archive --format=zip --prefix=MCU-StudioX/ -o $sourceArchive HEAD
    if($LASTEXITCODE -ne 0){throw 'Source archive failed.'}
    $checksum=Join-Path $output 'SHA256SUMS.txt'
    Write-StudioXReleaseChecksums @($file.FullName,$sourceArchive) $checksum
    $tag=$ReleaseTag
    # 不自动提交、打 tag、推送分支；已审核源码必须先有与 HEAD 对应的远端 tag。
    & git fetch origin "refs/tags/${tag}:refs/tags/${tag}"
    if($LASTEXITCODE -ne 0 -or (& git rev-list -n 1 $tag).Trim() -ne $head){throw 'Remote release tag must already resolve to this reviewed source commit.'}
    & gh release create $tag $file.FullName $sourceArchive $checksum ([IO.Path]::GetFullPath($ArtifactRecord)) --repo $Repository --verify-tag --draft --title "MCU StudioX $ReleaseVersion" --notes-file ([IO.Path]::GetFullPath($ReleaseNotes))
    if($LASTEXITCODE -ne 0){throw 'GitHub draft release failed; inspect original diagnostics before retrying.'}
}
finally{Pop-Location}
