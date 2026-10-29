param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $repo '.artifacts/rt51-hardware-20261011/startup-checks'
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$bin = Join-Path $repo 'artifacts/tool-runtime/toolsets/stc.sdcc/1.0.0/sdcc/bin'
$env:PATH = $bin + ';' + [Environment]::GetFolderPath('System')
$common = @('-mmcs51', '--model-large', '--stack-auto', '--debug')
function Compile([string[]]$arguments, [string]$log)
{
    $text = (& (Join-Path $bin 'sdcc.exe') @arguments 2>&1 | Out-String)
    [IO.File]::WriteAllText($log, $text)
    if ($LASTEXITCODE -ne 0 -or $text -match '(?i)(warning|error|undefined global)')
    {
        throw $text
    }
}
Compile ($common + @('-c', (Join-Path $repo 'firmware/StudioX.Rt51/examples/iap15/stc15_xinit_sdcc.c'), '-o', (Join-Path $output 'xinit.rel'))) (Join-Path $output 'xinit-build.log')
$results = @()
foreach ($case in 0..3)
{
    $image = Join-Path $output "case-$case.ihx"
    Compile ($common + @('--xram-loc', '1', '--xram-size', '1023', '--iram-size', '256', "-DXINIT_CASE=$case", (Join-Path $repo 'firmware/StudioX.Rt51/tests/iap15_xinit.c'), (Join-Path $output 'xinit.rel'), '-o', $image)) (Join-Path $output "case-$case-build.log")
    $map = Get-Content -LiteralPath (Join-Path $output "case-$case.map") -Raw
    $address = [Convert]::ToInt32([regex]::Match($map, '(?m)^D:\s+([0-9A-Fa-f]{8})\s+_test_result\s').Groups[1].Value, 16)
    if ($case -gt 0 -and $map -notmatch 's_XISEG\s' )
    {
        throw 'Missing XISEG'
    }
    $commands = @('fill xram 0x400 0x6ff 0xa5', 'step 50000', "expression /d xram[$address]", 'dump xram 0x400 0x6ff 16', 'quit')
    $commands | Set-Content -LiteralPath (Join-Path $output "case-$case.commands") -Encoding ascii
    $trace = ($commands | & (Join-Path $bin 's51.exe') -q -t C52 -X 12M -c - $image 2>&1 | Out-String)
    [IO.File]::WriteAllText((Join-Path $output "case-$case-simulation.log"), $trace)
    if ($LASTEXITCODE -ne 0 -or $trace -notmatch '(?m)^90\s*$')
    {
        throw "Startup case $case failed: $trace"
    }
    $sentinels = [regex]::Matches($trace, '(?m)^0x0[456][0-9a-f]{2}\s+((?:[0-9a-f]{2} ){15}[0-9a-f]{2})\s')
    if ($sentinels.Count -ne 48 -or ($sentinels | Where-Object { $_.Groups[1].Value -ne ('a5 ' * 15 + 'a5') }))
    {
        throw 'Monitor XRAM modified'
    }
    $results += [ordered]@{case =$case;
        passed                  =$true;
        hardware                =$false;
        imageSha256             =(Get-FileHash -LiteralPath $image).Hash;
        monitorXramIntact       =$true
    }
    Write-Output "PASS XINIT case $case"
}
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding utf8
