param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release-Version.ps1')
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output)
{
    throw 'Use a new validation directory.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
function Check([bool]$Passed, [string]$Description)
{
    if (!$Passed)
    {
        throw $Description
    }
    $checks.Add($Description)
}
foreach ($pair in @(
        @('0.2.5', '0.2.5.0'), @('0.2.5.3', '0.2.5.3'),
        @('0.2.5.4A', '0.2.5.4'), @('0.2.6LTS', '0.2.6.11'),
        @('65535.65535.65535.65535Z', '65535.65535.65535.65535')))
{
    $value = Get-StudioXReleaseVersion $pair[0]
    Check ($value.ProductVersion -eq $pair[0] -and $value.FileVersion -eq $pair[1]) "Identity $($pair[0])"
}
foreach ($invalid in @('', '0.2', '0.2.5.4.1', '0.2.5.4a', '0.2.5.4AB', '0.2.5.4-A',
        '65536.2.5', '000000.2.5', '0.2.5.4A ', '0.2.6lts', '0.2.6LTS ', '0.2.5LTS'))
{
    $rejected = $false
    try
    {
        Get-StudioXReleaseVersion $invalid | Out-Null
    }
    catch
    {
        $rejected = $true
    }
    Check $rejected "Reject invalid identity '$invalid'"
}
foreach ($pair in @(
        @('0.2.5.3', '0.2.5.4A'), @('0.2.5.4', '0.2.5.4A'),
        @('0.2.5.4A', '0.2.5.4B'), @('0.2.5.4Z', '0.2.5.5'),
        @('0.2.5.4Z', '0.2.6'), @('0.2.6.10', '0.2.6LTS'),
        @('0.2.6.11', '0.2.6LTS'), @('0.2.6LTS', '0.2.6.11Z'), @('0.2.6LTS', '0.2.6.12'),
        @('0.2.6LTS', '0.2.7')))
{
    Check ((Compare-StudioXReleaseVersions $pair[0] $pair[1]) -lt 0) "Order $($pair[0]) < $($pair[1])"
    Check ((Compare-StudioXReleaseVersions $pair[1] $pair[0]) -gt 0) "Reverse order $($pair[1]) > $($pair[0])"
}
Check ((Compare-StudioXReleaseVersions '0.2.5.4A' '0.2.5.4A') -eq 0) 'Same letter revision can repair'
Check ((Compare-StudioXReleaseVersions '0.2.5' '0.2.5.0') -eq 0) 'Three and four numeric parts compare equally'
Check ((Compare-StudioXReleaseVersions '0.2.6LTS' '0.2.6LTS') -eq 0) 'Same LTS identity can repair'
@{ status  = 'passed';
    checks = $checks;
    count  = $checks.Count
} | ConvertTo-Json -Depth 4 |
    Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
Write-Output "$($checks.Count) release version checks passed."
