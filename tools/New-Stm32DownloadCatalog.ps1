param([string]$PackDirectory = (Join-Path $PSScriptRoot '../artifacts/packs/STM32-0.1.1'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$devices = @()
$sources = @()
foreach ($file in Get-ChildItem -LiteralPath $PackDirectory -Filter '*.mcupack' | Sort-Object Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        function Read-Entry([string]$name) {
            $reader = [IO.StreamReader]::new($zip.GetEntry($name).Open())
            try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        $manifest = (Read-Entry 'manifest.json') | ConvertFrom-Json
        $hashes = (Read-Entry 'files.sha256.json') | ConvertFrom-Json
        foreach ($device in $manifest.devices) {
            $script = Read-Entry $device.openOcd.targetScript
            $entry = $zip.GetEntry($device.openOcd.targetScript).Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry)).ToLowerInvariant() } finally { $entry.Dispose() }
            if ($hash -ne $hashes.($device.openOcd.targetScript)) { throw 'Target script hash mismatch' }
            $devices += [ordered]@{
                id = $device.id; architecture = $device.architecture
                flashOrigin = $device.flashOrigin; flashBytes = $device.flashBytes
                ramOrigin = $device.ramOrigin; ramBytes = $device.ramBytes; targetScript = $script
            }
        }
        $sources += [ordered]@{ file = $file.Name; sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    } finally { $zip.Dispose() }
}
if ($devices.Count -ne 244) { throw "Expected the pinned F1/F4 catalog (244 devices), got $($devices.Count)." }
$destination = Join-Path $PSScriptRoot '../src/StudioX.Engine/Resources/stm32-download.json'
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
@{ formatVersion = 1; sources = $sources; devices = $devices } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8
Write-Output "Generated $($devices.Count) download profiles from pinned StudioX packs."
