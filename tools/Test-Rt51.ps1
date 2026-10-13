param(
    [string]$SdccDirectory,
    [string]$OutputDirectory,
    [ValidateSet('Default','Size','Speed','Low')][string]$Optimization = 'Default',
    [int[]]$Cases = (0..30),
    [switch]$Compact
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (!$SdccDirectory) { $SdccDirectory = Join-Path $repo 'artifacts/tool-runtime/toolsets/stc.sdcc/1.0.0/sdcc' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repo '.artifacts/rt51-20261011/validation' }
$sdccRoot = [IO.Path]::GetFullPath($SdccDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $output | Out-Null
$compiler = Join-Path $sdccRoot 'bin/sdcc.exe'
$simulator = Join-Path $sdccRoot 'bin/s51.exe'
# 工具仅在本进程中进入 PATH；不会修改用户或系统环境。
$env:PATH = (Join-Path $sdccRoot 'bin') + ';' + [Environment]::GetFolderPath('System')
$include = Join-Path $repo 'firmware/StudioX.Rt51/include'
$common = @('-mmcs51', '--model-large', '--stack-auto', '--debug', '--int-long-reent', '--float-reent', '-I', $include)
$common += switch ($Optimization) {
    'Size' { '--opt-code-size' }
    'Speed' { '--opt-code-speed' }
    'Low' { '--no-peep'; '--nogcse'; '--noinvariant'; '--noinduction'; '--noloopreverse'; '--nolabelopt'; '--nolospre' }
}
if ($Compact) {
    $common += @('-DRT51_MAX_TIMERS=0','-DRT51_ENABLE_STATS=0')
    $Cases = @($Cases | Where-Object { ($_ -lt 23 -or $_ -gt 25) -and $_ -lt 28 })
}
function Invoke-Compiler([string[]]$arguments, [string]$log) {
    $text = (& $compiler @arguments 2>&1 | Out-String)
    [IO.File]::WriteAllText($log, $text)
    if ($LASTEXITCODE -ne 0 -or $text -match '(?i)(warning|error|undefined global)') { throw "SDCC failed or emitted a diagnostic: $text" }
}
function Address([string]$map, [string]$symbol) {
    $match = [regex]::Match($map, '(?m)^(?:[CD]:\s*)?\s*([0-9A-Fa-f]{8})\s+' + [regex]::Escape($symbol) + '\s')
    if (!$match.Success) { throw "Missing symbol $symbol" }
    return [Convert]::ToInt32($match.Groups[1].Value, 16)
}
function Invoke-Simulator([string]$image, [string[]]$commands, [string]$log) {
    $start = [Diagnostics.ProcessStartInfo]::new($simulator)
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.CreateNoWindow = $true
    foreach ($argument in @('-q','-t','C52','-X','12M','-c','-',$image)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (!$process.Start()) { throw 'Cannot start uCsim' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        foreach ($command in $commands) { $process.StandardInput.WriteLine($command) }
        $process.StandardInput.Close()
        if (!$process.WaitForExit(15000)) { $process.Kill($true); throw 'uCsim exceeded the 15 second wall-clock limit' }
        $trace = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($log, $trace)
        if ($process.ExitCode -ne 0) { throw "uCsim failed: $trace" }
        return $trace
    }
    finally { $process.Dispose() }
}
foreach ($source in @('rt51', 'rt51_port_sdcc', 'rt51_timers')) {
    Invoke-Compiler ($common + @('-c', (Join-Path $repo "firmware/StudioX.Rt51/src/$source.c"), '-o', (Join-Path $output "$source.rel"))) (Join-Path $output "$source-build.log")
}
$reports = @()
foreach ($case in $Cases) {
    $stem = "case-$case"
    $image = Join-Path $output "$stem.ihx"
    $fixture = if ($case -eq 0) { 'preemption.c' } elseif ($case -ge 17) { 'extensions.c' } else { 'behavior.c' }
    $args = $common + @('--iram-size','256','--idata-loc','48','--stack-size','128','--xram-loc','1','--xram-size','1023','--code-size','56317',"-DTEST_CASE=$case",
        (Join-Path $repo "firmware/StudioX.Rt51/tests/$fixture"), (Join-Path $output 'rt51.rel'), (Join-Path $output 'rt51_port_sdcc.rel'), (Join-Path $output 'rt51_timers.rel'), '-o', $image)
    Invoke-Compiler $args (Join-Path $output "$stem-build.log")
    $map = Get-Content -LiteralPath (Join-Path $output "$stem.map") -Raw
    $resultAddress = Address $map '_test_result'
    $faultAddress = Address $map '_rt51_fault'
    $ticksAddress = Address $map '_rt51_tick_count'
    $checksAddress = Address $map '_check_count'
    $peakAddress = Address $map '_rt51_kernel_stack_peak'
    $currentAddress = Address $map '_rt51_current'
    $memory = Get-Content -LiteralPath (Join-Path $output "$stem.mem") -Raw
    if ($memory -notmatch 'Stack starts at: 0x80' -or $memory -notmatch 'with 128 bytes available') { throw 'Unexpected hardware stack placement' }
    $commands = @('fill xram 0x400 0x6ff 0xa5','step 5000000', "expression /d xram[$resultAddress]", "expression /d sfr[0x81]", "expression /d iram[$faultAddress]",
        "expression /d xram[$checksAddress]", "expression /d xram[$ticksAddress]", "expression /d xram[$($ticksAddress+1)]",
        "expression /d iram[$peakAddress]", 'expression /d sfr[0xb0]', "expression /d iram[$currentAddress]", 'dump xram 0x400 0x6ff 16', 'quit')
    $commands | Set-Content -LiteralPath (Join-Path $output "$stem-simulator.commands") -Encoding ascii
    $trace = Invoke-Simulator $image $commands (Join-Path $output "$stem-simulation.log")
    $values = @([regex]::Matches($trace, '(?m)^\s*([0-9]+)\s*$') | ForEach-Object { [int]$_.Groups[1].Value })
    if ($values.Count -ne 9) { throw "Unexpected simulator response: $trace" }
    $passed = if ($case -eq 10) { $values[2] -eq 1 } elseif ($case -eq 12) { $values[2] -eq 2 } elseif ($case -eq 14) { $values[2] -eq 4 } elseif ($case -eq 29) { $values[2] -eq 5 } else { $values[0] -eq 90 -and $values[2] -eq 0 }
    $protected = [regex]::Matches($trace, '(?m)^0x0[456][0-9a-f]{2}\s+((?:[0-9a-f]{2} ){15}[0-9a-f]{2})\s')
    $monitorIntact = $protected.Count -eq 48 -and @($protected | Where-Object { $_.Groups[1].Value -ne ('a5 '*15 + 'a5') }).Count -eq 0
    $passed = $passed -and $monitorIntact -and $values[7] -eq 255 -and $values[6] -lt 80
    if ($case -eq 15) { $passed = $passed -and $values[8] -eq 3 }
    $tickCount = $values[4] + 256 * $values[5]
    $reports += [ordered]@{ case=$case; optimization=$Optimization; passed=$passed; result=$values[0]; sp=$values[1]; fault=$values[2]; checks=$values[3]; ticks=$tickCount; kernelStackPeak=$values[6]; monitorXramIntact=$monitorIntact; p3=$values[7]; current=$values[8]; imageSha256=(Get-FileHash $image).Hash }
    Write-Output "RT51 case $case : pass=$passed result=$($values[0]) fault=$($values[2]) checks=$($values[3]) ticks=$tickCount"
}
$reports | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8
if ($reports | Where-Object { !$_.passed }) { throw 'RT51 executable validation failed; see the preserved simulation logs.' }
