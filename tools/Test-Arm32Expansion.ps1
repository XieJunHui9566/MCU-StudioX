param(
    [Parameter(Mandatory = $true)][string]$Packages,
    [Parameter(Mandatory = $true)][string]$Runtime,
    [Parameter(Mandatory = $true)][string]$Validator,
    [Parameter(Mandatory = $true)][string]$Output,
    [ValidateRange(1, 6)][int]$Workers = 4,
    [switch]$Preflight
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $root)
{
    throw 'Validation output must be a new directory.'
}
[IO.Directory]::CreateDirectory($root) | Out-Null
$archives = @(Get-ChildItem -LiteralPath ([IO.Path]::GetFullPath($Packages)) -Filter '*.mcupack' -File | Sort-Object Name)
$jobs = @()
for ($i = 0; $i -lt $Workers; $i++)
{
    $batch = @(for ($j = $i; $j -lt $archives.Count; $j += $Workers)
        {
            $archives[$j].FullName
        })
    if ($batch.Count -eq 0)
    {
        continue
    }
    $info = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute = $false;
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true;
    $info.RedirectStandardError = $true
    foreach ($item in @([IO.Path]::GetFullPath($Validator), [IO.Path]::GetFullPath($Runtime), (Join-Path $root "batch-$i")) + $batch)
    {
        $info.ArgumentList.Add($item)
    }
    if ($Preflight)
    {
        $info.Environment['STUDIOX_PACK_PREFLIGHT'] = '1'
    }
    $process = [Diagnostics.Process]::Start($info)
    $stdout = [IO.File]::Create((Join-Path $root "batch-$i.stdout.log"));
    $stderr = [IO.File]::Create((Join-Path $root "batch-$i.stderr.log"))
    $jobs += @{ Process =$process;
        Out             =$stdout;
        Error           =$stderr;
        OutTask         =$process.StandardOutput.BaseStream.CopyToAsync($stdout);
        ErrorTask       =$process.StandardError.BaseStream.CopyToAsync($stderr);
        Batch           =$i;
        Count           =$batch.Count
    }
}
$codes = @()
foreach ($job in $jobs)
{
    $job.Process.WaitForExit()
    $null = $job.OutTask.GetAwaiter().GetResult();
    $null = $job.ErrorTask.GetAwaiter().GetResult()
    $job.Out.Dispose();
    $job.Error.Dispose()
    $codes += @{batch =$job.Batch;
        packages      =$job.Count;
        exitCode      =$job.Process.ExitCode
    }
    Write-Output "Batch $($job.Batch): $($job.Count) packs, exit $($job.Process.ExitCode)"
    $job.Process.Dispose()
}
$codes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $root 'process-results.json')
if (@($codes | Where-Object exitCode -ne 0).Count -gt 0)
{
    exit 1
}
