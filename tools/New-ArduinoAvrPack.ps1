param([Parameter(Mandatory)][string]$PackagingCli, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$taskWorkspace = Split-Path $PSScriptRoot
if (!$OutputDirectory)
{
    $OutputDirectory = Join-Path $taskWorkspace 'artifacts/packs/Arduino-0.1.0'
}
$taskOutput = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $taskOutput)
{
    throw 'Pack output must be a new directory.'
}
New-Item -ItemType Directory -Path $taskOutput | Out-Null
$taskArchive = Join-Path $taskOutput 'arduino.avr-boards-0.1.0.mcupack'
& $PackagingCli pack (Join-Path $taskWorkspace 'examples/packs/arduino.avr') $taskArchive
if ($LASTEXITCODE -ne 0)
{
    throw 'Arduino AVR pack validation/packing failed.'
}
@(@{ id = 'arduino.avr-boards'; version = '0.1.0'; file = [IO.Path]::GetFileName($taskArchive);
        devices = @('ATmega328P'); sha256 = (Get-FileHash -LiteralPath $taskArchive).Hash
    }) | ConvertTo-Json -Depth 5 -AsArray |
    Set-Content -LiteralPath (Join-Path $taskOutput 'index.json')
