param([Parameter(Mandatory=$true)][string]$Destination)
$ErrorActionPreference = 'Stop'
$cache = [IO.Path]::GetFullPath($Destination)
[IO.Directory]::CreateDirectory($cache) | Out-Null
# 仅供维护者显式调用；IDE 导入 .mcupack 时不联网、不安装 SDK。
$items = @(
    @{ Name='Keil.STM32F1xx_DFP.2.4.1.pack'; Url='https://www.keil.com/pack/Keil.STM32F1xx_DFP.2.4.1.pack'; Hash='807ea15da5b172b916bbc47b2b87f1e621240ad208d38e82a417a2ef8191e9d1'; Folder='f1-dfp' },
    @{ Name='STM32F4xx_DSP_StdPeriph_Lib_V1.8.0.zip'; Url='https://downloads.sourceforge.net/project/micro-os-plus/Vendor%20Archives/STM32/STM32F4xx_DSP_StdPeriph_Lib_V1.8.0.zip'; Hash='6c215f9847c2d7229ea6eeeb545ad27437fcc0d8c0d5a0e5d792c4aba08cbbb0'; Folder='f4-spl' },
    @{ Name='Keil.STM32F4xx_DFP.pdsc'; Url='https://www.keil.com/pack/Keil.STM32F4xx_DFP.pdsc'; Hash='ec02212e78841a217c4e60ea8be110867f3a99080624fc68747b98697447f5a7' }
)
foreach ($item in $items) {
    $file = Join-Path $cache $item.Name
    if (!(Test-Path -LiteralPath $file)) {
        $partial = "$file.partial"
        & curl.exe --fail --location --silent --show-error --retry 2 --connect-timeout 15 --max-time 300 --output $partial $item.Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($item.Url)" }
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $item.Hash) { throw "Source hash mismatch: $partial" }
        Move-Item -LiteralPath $partial -Destination $file
    }
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $item.Hash) { throw "Source hash mismatch: $file" }
    if ($item.Folder) {
        $directory = Join-Path $cache $item.Folder
        if (!(Test-Path -LiteralPath $directory)) { Expand-Archive -LiteralPath $file -DestinationPath $directory }
    }
    Write-Output "Verified $($item.Name)"
}
