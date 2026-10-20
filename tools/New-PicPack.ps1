param([Parameter(Mandatory)][string]$PackagingCli, [Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Pack output must be a new directory.' }
New-Item -ItemType Directory -Path $output | Out-Null
$entries = foreach ($source in Get-ChildItem -LiteralPath (Join-Path $root 'examples/packs') -Directory -Filter 'microchip.pic16*' | Sort-Object Name) {
    $manifest = Get-Content -LiteralPath (Join-Path $source.FullName 'manifest.json') -Raw | ConvertFrom-Json
    $archive = Join-Path $output ($manifest.id + '-' + $manifest.version + '.mcupack')
    & $PackagingCli pack $source.FullName $archive | Out-Host
    if ($LASTEXITCODE) { throw 'PIC pack validation failed.' }
    @{id=$manifest.id;version=$manifest.version;file=[IO.Path]::GetFileName($archive);devices=@($manifest.devices.id);sha256=(Get-FileHash -LiteralPath $archive).Hash}
}
@($entries) | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'index.json') -Encoding utf8
