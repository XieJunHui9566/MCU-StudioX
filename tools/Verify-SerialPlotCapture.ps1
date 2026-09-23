param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$lines = [IO.File]::ReadAllLines((Resolve-Path -LiteralPath $Path))
if ($lines.Count -lt 3 -or $lines[0] -notmatch '^# MCU StudioX serial plot;' -or $lines[0] -match 'SIMULATED') {
    throw 'Expected a hardware serial plot CSV, not a demo capture.'
}
if ($lines[1] -ne 'time_s,segment,CH1,CH2,CH3,CH4') { throw 'Expected four test channels.' }
$rows = @($lines[1..($lines.Count - 1)] | ConvertFrom-Csv)
$mismatches = 0
$sequenceGaps = 0
$backwardTimes = 0
$previousSequence = $null
$previousTime = $null
foreach ($row in $rows) {
    $n = [long]::Parse($row.CH4, $culture)
    $phase = $n % 400
    $triangle = [Math]::Floor($(if ($phase -lt 200) { $phase } else { 400 - $phase }) * 4095 / 200)
    $decimal = (($n * 7) % 1000 - 500) / 10.0
    $pulse = if ($n % 100 -lt 10) { 4095 } else { 0 }
    if ([double]::Parse($row.CH1, $culture) -ne $triangle -or
        [Math]::Abs([double]::Parse($row.CH2, $culture) - $decimal) -gt 1e-8 -or
        [double]::Parse($row.CH3, $culture) -ne $pulse) { $mismatches++ }
    if ($null -ne $previousSequence -and $n -ne $previousSequence + 1) { $sequenceGaps++ }
    $time = [double]::Parse($row.time_s, $culture)
    if ($null -ne $previousTime -and $time -lt $previousTime) { $backwardTimes++ }
    $previousSequence = $n
    $previousTime = $time
}
$span = [double]::Parse($rows[-1].time_s, $culture) - [double]::Parse($rows[0].time_s, $culture)
$result = [ordered]@{
    File = (Resolve-Path -LiteralPath $Path).Path
    Clock = $lines[0]
    Rows = $rows.Count
    FirstSequence = $rows[0].CH4
    LastSequence = $rows[-1].CH4
    ValueMismatches = $mismatches
    SequenceGaps = $sequenceGaps
    BackwardTimestamps = $backwardTimes
    Segments = @($rows.segment | Select-Object -Unique).Count
    DurationSeconds = $span
    SamplesPerSecond = if ($span -gt 0) { ($rows.Count - 1) / $span } else { 0 }
    SHA256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
$result | ConvertTo-Json
if ($mismatches -or $sequenceGaps -or $backwardTimes) { throw 'Hardware plot capture did not match the firmware sequence.' }
