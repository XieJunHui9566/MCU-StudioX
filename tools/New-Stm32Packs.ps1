param(
    [Parameter(Mandatory=$true)][string]$CubeF1Directory,
    [Parameter(Mandatory=$true)][string]$CubeF4Directory,
    [Parameter(Mandatory=$true)][string]$SourceDirectory,
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$AggregateForValidation
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null
$source = [IO.Path]::GetFullPath($SourceDirectory)
$rtos = Join-Path $CubeF4Directory 'Middlewares/Third_Party/FreeRTOS/Source'
foreach ($family in @('f1','f4')) {
    $cube = if ($family -eq 'f1') { $CubeF1Directory } else { $CubeF4Directory }
    $spl = if ($family -eq 'f1') { Join-Path $source 'f1-dfp/Device' } else { Join-Path $source 'f4-spl/STM32F4xx_DSP_StdPeriph_Lib_V1.8.0/Libraries' }
    $pdsc = if ($family -eq 'f1') { Join-Path $source 'f1-dfp/Keil.STM32F1xx_DFP.pdsc' } else { Join-Path $source 'Keil.STM32F4xx_DFP.pdsc' }
    $destination = if ($AggregateForValidation) { Join-Path $output "studiox.stm32$family-0.1.1.mcupack" } else { $output }
    [string[]]$modeArguments = if ($AggregateForValidation) { @() } else { @('--split') }
    & python (Join-Path $PSScriptRoot 'New-Stm32SeriesPack.py') --series $family --cube $cube --spl $spl --pdsc $pdsc --provenance (Join-Path $root "examples/packs/st.stm32-series/$family-provenance.json") --rtos $rtos --output $destination @modeArguments
    if ($LASTEXITCODE -ne 0) { throw "STM32$family pack generation failed." }
}
if (!$AggregateForValidation) {
    & python (Join-Path $PSScriptRoot 'Index-Stm32Packs.py') $output
    if ($LASTEXITCODE -ne 0) { throw 'STM32 pack catalog generation failed.' }
}
