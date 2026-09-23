param(
    [Parameter(Mandatory=$true)][string]$CubeF4Directory,
    [Parameter(Mandatory=$true)][string]$SplDeviceDirectory,
    [string]$OutputFile
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$cube = [IO.Path]::GetFullPath($CubeF4Directory)
$spl = [IO.Path]::GetFullPath($SplDeviceDirectory)
$cli = Join-Path $projectRoot 'src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll'
$recipe = Join-Path $projectRoot 'examples/packs/st.stm32f407zg'
if (!$OutputFile) { $OutputFile = Join-Path $projectRoot 'artifacts/packs/studiox.stm32f407zg-0.1.1.mcupack' }
$output = [IO.Path]::GetFullPath($OutputFile)
if (Test-Path -LiteralPath $output) { throw 'Pack output already exists; use a new version or destination.' }
if (!(Test-Path -LiteralPath $cli)) { throw 'Build the StudioX CLI first.' }
$hal = Join-Path $cube 'Drivers/STM32F4xx_HAL_Driver'
$cmsis = Join-Path $cube 'Drivers/CMSIS'
$device = Join-Path $cmsis 'Device/ST/STM32F4xx'
$rtos = Join-Path $cube 'Middlewares/Third_Party/FreeRTOS/Source'
foreach ($file in @((Join-Path $hal 'Src/stm32f4xx_hal.c'), (Join-Path $spl 'StdPeriph_Driver/src/stm32f4xx_rcc.c'), (Join-Path $rtos 'tasks.c'))) {
    if (!(Test-Path -LiteralPath $file)) { throw "SDK input missing: $file" }
}
if ((Get-Content -LiteralPath (Join-Path $rtos 'include/task.h') -Raw) -notmatch 'FreeRTOS Kernel V10.3.1') { throw 'This recipe expects the CubeF4 FreeRTOS 10.3.1 kernel.' }
$halVersion = Get-Content -LiteralPath (Join-Path $hal 'Src/stm32f4xx_hal.c') -Raw
if ($halVersion -notmatch '__STM32F4xx_HAL_VERSION_MAIN\s+\(0x01U\)' -or $halVersion -notmatch '__STM32F4xx_HAL_VERSION_SUB1\s+\(0x08U\)' -or $halVersion -notmatch '__STM32F4xx_HAL_VERSION_SUB2\s+\(0x05U\)') { throw 'This recipe expects STM32F4 HAL 1.8.5.' }
if ((Get-Content -LiteralPath (Join-Path $spl 'StdPeriph_Driver/src/stm32f4xx_rcc.c') -Raw) -notmatch '@version V1.3.0') { throw 'This recipe expects STM32F4 SPL 1.3.0.' }
$staging = Join-Path $projectRoot ('.artifacts/stm32f407-pack-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
function Copy-Tree([string]$source, [string]$relative) {
    $target = Join-Path $staging $relative
    [IO.Directory]::CreateDirectory($target) | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $source -Recurse -File) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'SDK links are not supported.' }
        $destination = Join-Path $target ([IO.Path]::GetRelativePath($source, $item.FullName))
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $item.FullName -Destination $destination
    }
}
function Copy-File([string]$source, [string]$relative) {
    $destination = Join-Path $staging $relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}
Copy-Tree $recipe ''
Copy-Tree (Join-Path $cmsis 'Include') 'sdk/cmsis-core'
Copy-Tree (Join-Path $device 'Include') 'sdk/hal-device'
Copy-Tree (Join-Path $hal 'Inc') 'sdk/hal/Inc'
Copy-Tree (Join-Path $hal 'Src') 'sdk/hal/Src'
Copy-Tree (Join-Path $spl 'Include') 'sdk/spl-device'
Copy-Tree (Join-Path $spl 'StdPeriph_Driver/inc') 'sdk/spl/inc'
Copy-Tree (Join-Path $spl 'StdPeriph_Driver/src') 'sdk/spl/src'
Copy-File (Join-Path $device 'Source/Templates/gcc/startup_stm32f407xx.s') 'sdk/startup/startup_stm32f407xx.s'
Copy-File (Join-Path $device 'Source/Templates/system_stm32f4xx.c') 'sdk/startup/system_stm32f4xx.c'
Copy-Tree (Join-Path $rtos 'include') 'sdk/freertos/include'
Copy-Tree (Join-Path $rtos 'portable/GCC/ARM_CM4F') 'sdk/freertos/portable/GCC/ARM_CM4F'
foreach ($name in @('tasks.c','queue.c','list.c','timers.c','event_groups.c','stream_buffer.c','croutine.c')) { Copy-File (Join-Path $rtos $name) "sdk/freertos/$name" }
Copy-File (Join-Path $rtos 'portable/MemMang/heap_4.c') 'sdk/freertos/portable/MemMang/heap_4.c'
Copy-File (Join-Path $cmsis 'LICENSE.txt') 'licenses/CMSIS-LICENSE.txt'
Copy-File (Join-Path $device 'LICENSE.txt') 'licenses/STM32-CMSIS-LICENSE.txt'
Copy-File (Join-Path $hal 'LICENSE.txt') 'licenses/STM32-HAL-LICENSE.txt'
Copy-File (Join-Path $spl 'StdPeriph_Driver/MCD-ST Liberty SW License Agreement V2.pdf') 'licenses/STM32-SPL-License.pdf'
Copy-File (Join-Path $cube 'Package_license.md') 'licenses/STM32Cube-Package-license.md'
# FreeRTOS 的 MIT 许可完整保留在各源码头部，额外保留一个独立许可副本。
$taskHeader = Get-Content -LiteralPath (Join-Path $rtos 'tasks.c') -Raw
$taskHeader.Substring(0, $taskHeader.IndexOf('*/') + 2) | Set-Content -LiteralPath (Join-Path $staging 'licenses/FreeRTOS-MIT.txt') -Encoding utf8

$enabled = @('HAL','HAL_RCC','HAL_GPIO','HAL_EXTI','HAL_DMA','HAL_CORTEX','HAL_PWR','HAL_FLASH','HAL_TIM','HAL_UART','HAL_USART','HAL_SPI','HAL_I2C','HAL_ADC')
$halConfig = Get-Content -LiteralPath (Join-Path $hal 'Inc/stm32f4xx_hal_conf_template.h') -Raw
$halConfig = [regex]::Replace($halConfig, '(?m)^#define (HAL(?:_[A-Z0-9]+)?)(_MODULE_ENABLED).*$', {
    param($match)
    if ($match.Groups[1].Value -in $enabled) { return $match.Value }
    return '/* ' + $match.Value + ' */'
})
$halConfig = $halConfig.Replace('25000000U', '8000000U')
[IO.Directory]::CreateDirectory((Join-Path $staging 'templates/hal')) | Out-Null
[IO.File]::WriteAllText((Join-Path $staging 'templates/hal/stm32f4xx_hal_conf.h'), $halConfig, [Text.UTF8Encoding]::new($false))
$timebase = Get-Content -LiteralPath (Join-Path $hal 'Src/stm32f4xx_hal_timebase_tim_template.c') -Raw
$timebase = $timebase.Replace('  HAL_IncTick();', '  if (htim->Instance == TIM6) HAL_IncTick();')
[IO.File]::WriteAllText((Join-Path $staging 'templates/hal/hal_timebase_tim6.c'), $timebase, [Text.UTF8Encoding]::new($false))

$splModules = @('misc','stm32f4xx_adc','stm32f4xx_can','stm32f4xx_crc','stm32f4xx_dac','stm32f4xx_dbgmcu','stm32f4xx_dcmi','stm32f4xx_dma','stm32f4xx_exti','stm32f4xx_flash','stm32f4xx_fsmc','stm32f4xx_gpio','stm32f4xx_i2c','stm32f4xx_iwdg','stm32f4xx_pwr','stm32f4xx_rcc','stm32f4xx_rng','stm32f4xx_rtc','stm32f4xx_sdio','stm32f4xx_spi','stm32f4xx_syscfg','stm32f4xx_tim','stm32f4xx_usart','stm32f4xx_wwdg')
$splConfig = "#ifndef STUDIOX_STM32F4XX_CONF_H`n#define STUDIOX_STM32F4XX_CONF_H`n" + (($splModules | ForEach-Object { '#include "' + $_ + '.h"' }) -join "`n") + "`n#define assert_param(expression) ((void)0U)`n#endif`n"
[IO.Directory]::CreateDirectory((Join-Path $staging 'templates/spl')) | Out-Null
[IO.File]::WriteAllText((Join-Path $staging 'templates/spl/stm32f4xx_conf.h'), $splConfig, [Text.UTF8Encoding]::new($false))

$halSources = @(Get-ChildItem -LiteralPath (Join-Path $staging 'sdk/hal/Src') -Filter '*.c' -File | Where-Object Name -NotLike '*template*' | Sort-Object Name | ForEach-Object { 'sdk/hal/Src/' + $_.Name })
$rtosSources = @('tasks.c','queue.c','list.c','timers.c','event_groups.c','stream_buffer.c','croutine.c','portable/GCC/ARM_CM4F/port.c','portable/MemMang/heap_4.c') | ForEach-Object { 'sdk/freertos/' + $_ }
$templates = @()
foreach ($library in @('hal','spl')) {
    foreach ($withRtos in @($false,$true)) {
        $id = $library + $(if ($withRtos) { '-freertos' } else { '' })
        $title = $(if ($library -eq 'hal') { 'HAL' } else { '标准外设库 SPL' }) + $(if ($withRtos) { ' + FreeRTOS' } else { ' · 裸机' })
        $defines = @(); $includes = @(); $sources = @()
        $files = [ordered]@{ 'include/board.h'='templates/common/board.h'; 'src/board_clock.c'='templates/common/board_clock.c'; 'src/stm32f4xx_it.c'='templates/common/interrupts.c' }
        if ($library -eq 'hal') {
            $defines = @('STM32F407xx','USE_HAL_DRIVER')
            $includes = @('sdk/hal-device','sdk/hal/Inc','sdk/hal/Inc/Legacy')
            $sources = $halSources
            $files['include/stm32f4xx_hal_conf.h'] = 'templates/hal/stm32f4xx_hal_conf.h'
        } else {
            $defines = @('STM32F40_41xxx','STM32F407xx','USE_STDPERIPH_DRIVER')
            $includes = @('sdk/spl-device','sdk/spl/inc')
            $sources = @($splModules | ForEach-Object { 'sdk/spl/src/' + $_ + '.c' })
            $files['include/stm32f4xx_conf.h'] = 'templates/spl/stm32f4xx_conf.h'
        }
        if ($withRtos) {
            $defines += 'STUDIOX_USE_FREERTOS=1'
            $includes += @('sdk/freertos/include','sdk/freertos/portable/GCC/ARM_CM4F')
            $sources += $rtosSources
            $files['include/FreeRTOSConfig.h'] = 'templates/common/FreeRTOSConfig.h'
            $files['src/rtos_hooks.c'] = 'templates/common/rtos_hooks.c'
            if ($library -eq 'hal') { $files['src/hal_timebase_tim6.c'] = 'templates/hal/hal_timebase_tim6.c' }
        }
        $templates += [ordered]@{ id=$id; displayName=$title; description='外部 8 MHz 晶振，168 MHz 系统时钟；内存心跳示例，不预设板级引脚。'; entryFile='templates/common/main.c'; files=$files
            build=[ordered]@{ defines=$defines; includeDirectories=$includes; sources=$sources; compileOptions=@(); linkOptions=@() } }
    }
}
$manifest = [ordered]@{ formatVersion=1; id='studiox.stm32f407zg'; version='0.1.0'; displayName='STM32F407ZG · HAL / 标准库 / FreeRTOS'; vendor='STMicroelectronics / StudioX templates'; devices=@(
    [ordered]@{ id='STM32F407ZGT6'; displayName='STM32F407ZGT6 · Cortex-M4F · 168 MHz'; architecture='arm'; flashOrigin=0x08000000; flashBytes=1048576; ramOrigin=0x20000000; ramBytes=131072
        toolsetId='arm.gnu'; toolsetVersion='1.0.0'; compilerId='arm-gnu-15.2.rel1'
        cpuFlags=@('-mcpu=cortex-m4','-mthumb','-mfpu=fpv4-sp-d16','-mfloat-abi=hard'); defines=@('HSE_VALUE=8000000U','USER_VECT_TAB_ADDRESS'); includeDirectories=@('sdk/cmsis-core')
        sources=@('sdk/startup/startup_stm32f407xx.s','sdk/startup/system_stm32f4xx.c','support/runtime.c'); linkerScript='linker/stm32f407zg.ld'
        compileOptions=@('-Og','-g3','-ffunction-sections','-fdata-sections','-fno-common','--specs=nano.specs'); linkOptions=@('-nostartfiles','--specs=nano.specs','--specs=nosys.specs','-Wl,--gc-sections')
        templates=$templates
        openOcd=[ordered]@{ targetScript='debug/stm32f407zg.cfg'; probes=@(
            @{ id='stlink'; displayName='ST-Link'; interfaceScript='interface/stlink.cfg'; transport='swd'; defaultSpeedKhz=2000 },
            @{ id='cmsis-dap'; displayName='CMSIS-DAP'; interfaceScript='interface/cmsis-dap.cfg'; transport='swd'; defaultSpeedKhz=2000 },
            @{ id='jlink'; displayName='J-Link'; interfaceScript='interface/jlink.cfg'; transport='swd'; defaultSpeedKhz=2000 }
        ) }
    }
)}
$manifest | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $staging 'manifest.json') -Encoding utf8
@{ cube='STM32CubeF4 1.28.3'; hal='STM32F4 HAL 1.8.5'; spl='STM32F4 StdPeriph 1.3.0, Keil STM32F4xx_DFP 1.0.8'; freertos='FreeRTOS 10.3.1, GCC ARM_CM4F, heap_4'; notes='Unmodified vendor library sources. HAL configuration and TIM6 timebase derived from vendor templates; StudioX application/clock/linker/debug templates are independent. No tool binaries included.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $staging 'provenance.json') -Encoding utf8
& python (Join-Path $PSScriptRoot 'Stm32TemplateLayout.py') $staging
if ($LASTEXITCODE -ne 0) { throw 'STM32 system-layout generation failed.' }
& dotnet $cli pack $staging $output
if ($LASTEXITCODE -ne 0) { throw 'STM32 pack generation failed.' }
Get-Item -LiteralPath $output | Select-Object FullName,Length
