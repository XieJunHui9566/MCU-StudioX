param([Parameter(Mandatory)][string]$ValidationExe, [Parameter(Mandatory)][string]$Toolsets,
    [Parameter(Mandatory)][string]$Packs, [Parameter(Mandatory)][string]$Output)
$ErrorActionPreference = 'Stop'
$targetRoot = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $targetRoot) { throw 'Use a new validation directory.' }
New-Item -ItemType Directory -Path $targetRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$reports = @()
$failures = @()
foreach ($archive in Get-ChildItem -LiteralPath $Packs -Filter '*.mcupack' -File | Sort-Object Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($archive.FullName)
    try {
        $reader = [IO.StreamReader]::new($zip.GetEntry('manifest.json').Open())
        try { $manifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $zip.Dispose() }
    # 新增 LL 在每个子系列最小内存型号实编；常用 F103/F407 再覆盖全部六模板。
    $device = $manifest.devices | Sort-Object flashBytes, ramBytes, id | Select-Object -First 1
    $templates = 'll,ll-freertos'
    if ($manifest.id -eq 'studiox.stm32f103') { $device = $manifest.devices | Where-Object id -eq 'STM32F103C8'; $templates = 'hal,hal-freertos,spl,spl-freertos,ll,ll-freertos' }
    if ($manifest.id -eq 'studiox.stm32f407') { $device = $manifest.devices | Where-Object id -eq 'STM32F407ZG'; $templates = 'hal,hal-freertos,spl,spl-freertos,ll,ll-freertos' }
    $run = Join-Path $targetRoot $manifest.id
    $log = $run + '.log'
    & $ValidationExe $Toolsets $run $archive.FullName $device.id $templates *> $log
    $exit = $LASTEXITCODE
    if (Test-Path -LiteralPath (Join-Path $run 'results.json')) { $reports += @(Get-Content -LiteralPath (Join-Path $run 'results.json') -Raw | ConvertFrom-Json) }
    if ($exit -ne 0) { $failures += $manifest.id }
    [IO.File]::WriteAllText((Join-Path $targetRoot 'results.json'), (ConvertTo-Json -InputObject @($reports) -Depth 10), [Text.UTF8Encoding]::new($false))
    Write-Output "$($manifest.id): exit=$exit"
}
if ($failures.Count) { throw "Failed: $($failures -join ', ')" }
Write-Output "PASS $($reports.Count) builds; no hardware accessed."
