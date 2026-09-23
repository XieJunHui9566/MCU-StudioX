"""Maintainer-only pack recipe. Python stdlib; firmware/IDE have no Python dependency.

Inputs are pinned SDK directories and a Keil PDSC. No implicit network or hardware access.
"""
import argparse
import hashlib
import json
import re
import shutil
import subprocess
import uuid
import xml.etree.ElementTree as ET
from pathlib import Path
from Stm32TemplateLayout import VERSION, apply_layout

ROOT = Path(__file__).resolve().parents[1]
OLD = ROOT / 'examples/packs/st.stm32f407zg'
RECIPE = ROOT / 'examples/packs/st.stm32-series'


def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8', newline='\n')


def copy(src, dst):
    dst.parent.mkdir(parents=True, exist_ok=True)
    if src.is_dir():
        shutil.copytree(src, dst, dirs_exist_ok=True)
    else:
        shutil.copy2(src, dst)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def inherited(nodes, tag, attr):
    for node in reversed(nodes):
        child = node.find(tag)
        if child is not None and attr in child.attrib:
            return child.get(attr)
    raise ValueError(f'Missing {tag}.{attr}')


def devices(pdsc, series):
    family = ET.parse(pdsc).find('./devices/family')
    for sub in family.findall('subFamily'):
        for dev in sub.findall('device'):
            name = dev.get('Dname')
            memories = {int(m.get('start'), 0): int(m.get('size'), 0) for m in dev.findall('memory')}
            hz = int(inherited([family, sub, dev], 'processor', 'Dclock'))
            define = inherited([family, sub, dev], 'compile', 'define')
            if series == 'f1':
                suffix = ('xC' if name[6:9] in ('105', '107') else
                          'xB' if name[6:9] == '100' and name[-1] in '468B' else
                          'x6' if name[-1] in '46' else 'xB' if name[-1] in '8B' else
                          'xE' if name[-1] in 'CDE' else 'xG')
                hal_define, spl_define = name[:9] + suffix, define
            else:
                hal_define = define
                part = name[6:9]
                spl_define = ('STM32F40_41xxx' if part in ('405', '407', '415', '417') else
                              'STM32F427_437xx' if part in ('427', '437') else
                              'STM32F429_439xx' if part in ('429', '439') else
                              'STM32F469_479xx' if part in ('469', '479') else
                              'STM32F413_423xx' if part in ('413', '423') else
                              'STM32F411xE' if part == '411' else
                              'STM32F412xG' if part == '412' else name[:9] + 'xx')
            yield dict(id=name, subfamily=sub.get('DsubFamily'), hz=hz, hal=hal_define, spl=spl_define,
                       flash=memories[0x08000000], ram=memories[0x20000000], ccm=memories.get(0x10000000, 0))


def board_header(d, series):
    hz, part = d['hz'], d['id'][6:9]
    text = f'''#ifndef STUDIOX_BOARD_H
#define STUDIOX_BOARD_H
#include <stdint.h>
#ifdef USE_HAL_DRIVER
#include "stm32{series}xx_hal.h"
#else
#include "{'stm32f10x.h' if series == 'f1' else 'stm32f4xx.h'}"
#endif
/* {d['id']}：3.3 V，外部 8 MHz 无源晶振；无板级 LED/UART 假设。 */
#define BOARD_SYSCLK_HZ {hz}U
'''
    if series == 'f4':
        latency = (hz - 1) // 30000000
        n, p, q = (168, 4, 7) if hz == 84000000 else (hz // 1000000, 2, 7 if hz == 168000000 else 5 if hz == 100000000 else 8)
        apb1 = 4 if hz > 100000000 else 2
        apb2 = 2 if hz > 100000000 else 1
        text += f'''#define STUDIOX_F4 1
#define BOARD_PLL_N {n}U
#define BOARD_PLL_P {p}U
#define BOARD_PLL_Q {q}U
#define BOARD_APB1_BITS RCC_CFGR_PPRE1_DIV{apb1}
#define BOARD_APB2_BITS RCC_CFGR_PPRE2_DIV{apb2}
#define BOARD_FLASH_LATENCY {latency}U
'''
        if hz == 180000000:
            text += '#define STUDIOX_OVERDRIVE 1\n'
    else:
        multiplier = hz // (4000000 if hz == 36000000 else 8000000)
        pll_bits = (multiplier - 2) << 18 | (1 << 17 if hz == 36000000 else 0)
        text += f'''#define BOARD_PLL_BITS 0x{pll_bits:08x}U
#define BOARD_APB1_BITS RCC_CFGR_PPRE1_DIV{2 if hz > 36000000 else 1}
#define BOARD_FLASH_LATENCY {(hz - 1) // 24000000}U
'''
    return text + '''extern volatile uint32_t app_error_code;
void BoardClock_Init(void);
void StudioX_Panic(uint32_t code);
void Board_Delay(uint32_t milliseconds);
#endif
'''


def openocd(d, series):
    if series == 'f4':
        ids = {'401': 0x423 if d['hal'].endswith('xC') else 0x433, '405': 0x413, '407': 0x413,
               '415': 0x413, '417': 0x413, '410': 0x458, '411': 0x431, '412': 0x441,
               '413': 0x463, '423': 0x463, '427': 0x419, '429': 0x419, '437': 0x419,
               '439': 0x419, '446': 0x421, '469': 0x434, '479': 0x434}
        chip_id, flash_reg = ids[d['id'][6:9]], 0x1fff7a22
    else:
        chip_id = {'STM32F10X_LD': 0x412, 'STM32F10X_MD': 0x410, 'STM32F10X_HD': 0x414,
                   'STM32F10X_XL': 0x430, 'STM32F10X_LD_VL': 0x420, 'STM32F10X_MD_VL': 0x420,
                   'STM32F10X_HD_VL': 0x428, 'STM32F10X_CL': 0x418}[d['spl']]
        flash_reg = 0x1ffff7e0
    bank2 = 'flash bank $_CHIPNAME.flash1 stm32f1x 0x08080000 0 0 0 $_TARGETNAME\n' if series == 'f1' and d['spl'] == 'STM32F10X_XL' else ''
    return f'''# {d['id']}：先检查硅片组与标称 Flash 容量，再允许下载。
source [find target/stm32{series}x.cfg]
{bank2}\
$_TARGETNAME configure -event reset-init {{}}
$_TARGETNAME configure -event reset-start {{}}
proc studiox_check_target {{}} {{
    set id [expr {{[lindex [read_memory 0xe0042000 32 1] 0] & 0xfff}}]
    set kb [lindex [read_memory 0x{flash_reg:08x} 16 1] 0]
    if {{$id != 0x{chip_id:03x} || $kb != {d['flash'] // 1024}}} {{
        error "Expected {d['id']} (device ID 0x{chip_id:03x}, {d['flash'] // 1024} KiB Flash); got ID $id, $kb KiB"
    }}
}}
'''


def spl_modules(d, series):
    if series == 'f1':
        modules = 'adc bkp crc dbgmcu dma exti flash gpio i2c iwdg pwr rcc rtc spi tim usart wwdg'.split()
        if d['id'][6:9] in ('103', '105', '107'): modules += ['can']
        if d['spl'] in ('STM32F10X_HD', 'STM32F10X_XL', 'STM32F10X_HD_VL'): modules += ['fsmc', 'sdio']
        if d['spl'] in ('STM32F10X_HD', 'STM32F10X_XL', 'STM32F10X_CL', 'STM32F10X_LD_VL', 'STM32F10X_MD_VL', 'STM32F10X_HD_VL'): modules += ['dac']
        if d['spl'].endswith('_VL'): modules += ['cec']
        return ['misc'] + ['stm32f10x_' + m for m in modules]
    modules = 'adc crc dbgmcu dma exti flash gpio i2c iwdg pwr rcc rtc spi syscfg tim usart wwdg'.split()
    part = d['id'][6:9]
    if part != '410': modules += ['sdio']
    extras = {
        '401': '', '405': 'can dac dcmi fsmc rng', '407': 'can dac dcmi fsmc rng',
        '415': 'can dac dcmi fsmc rng cryp cryp_aes cryp_des cryp_tdes hash hash_md5 hash_sha1',
        '417': 'can dac dcmi fsmc rng cryp cryp_aes cryp_des cryp_tdes hash hash_md5 hash_sha1',
        '410': 'dac rng lptim fmpi2c', '411': 'flash_ramfunc',
        '412': 'rng can qspi fsmc dfsdm', '413': 'rng can qspi fsmc dfsdm fmpi2c',
        '423': 'rng can qspi fsmc dfsdm fmpi2c cryp cryp_aes cryp_des cryp_tdes',
        '427': 'can dac dcmi dma2d fmc rng sai', '429': 'can dac dcmi dma2d fmc rng sai ltdc',
        '437': 'can dac dcmi dma2d fmc rng sai cryp cryp_aes cryp_des cryp_tdes hash hash_md5 hash_sha1',
        '439': 'can dac dcmi dma2d fmc rng sai ltdc cryp cryp_aes cryp_des cryp_tdes hash hash_md5 hash_sha1',
        '446': 'can dac dcmi fmc sai fmpi2c qspi spdifrx cec',
        '469': 'can dac dcmi dma2d fmc rng sai ltdc qspi dsi',
        '479': 'can dac dcmi dma2d fmc rng sai ltdc qspi dsi cryp cryp_aes cryp_des cryp_tdes hash hash_md5 hash_sha1'
    }
    return ['misc'] + ['stm32f4xx_' + m for m in modules + extras[part].split()]


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--series', choices=['f1', 'f4'], required=True)
    p.add_argument('--cube', type=Path, required=True)
    p.add_argument('--spl', type=Path, required=True, help='F1 Keil Device or F4 SPL Libraries directory')
    p.add_argument('--pdsc', type=Path, required=True)
    p.add_argument('--provenance', type=Path, required=True)
    p.add_argument('--rtos', type=Path, required=True)
    p.add_argument('--output', type=Path, required=True)
    p.add_argument('--split', action='store_true', help='Output one immutable pack per STM32F103/F401/etc. subfamily')
    a = p.parse_args()
    if a.output.exists() and not a.split:
        raise ValueError('Output already exists. Use a new version/destination.')
    series = a.series
    stage = ROOT / '.artifacts' / ('stm32-' + series + '-' + uuid.uuid4().hex)
    stage.mkdir(parents=True)
    stem = 'stm32' + series + 'xx'
    hal = a.cube / 'Drivers' / (stem.upper().replace('XX', 'xx') + '_HAL_Driver')
    cmsis = a.cube / 'Drivers/CMSIS'
    device = cmsis / 'Device/ST' / stem.upper().replace('XX', 'xx')
    spl_driver = a.spl / ('StdPeriph_Driver' if series == 'f1' else 'STM32F4xx_StdPeriph_Driver')
    spl_device = a.spl if series == 'f1' else a.spl / 'CMSIS/Device/ST/STM32F4xx'
    spl_header = spl_device / 'Include' / ('stm32f10x.h' if series == 'f1' else 'stm32f4xx.h')
    spl_text = spl_header.read_text(encoding='latin-1')
    expected_hal = ('01', '01', '0A') if series == 'f1' else ('01', '08', '05')
    hal_c = (hal / 'Src' / (stem + '_hal.c')).read_text(encoding='latin-1')
    for field, value in zip(['MAIN', 'SUB1', 'SUB2'], expected_hal):
        if not re.search(r'HAL_VERSION_' + field + r'\s+\(0x' + value + r'U\)', hal_c, re.I):
            raise ValueError('HAL input version differs from provenance')
    spl_rcc = (spl_driver / 'src' / ('stm32f10x_rcc.c' if series == 'f1' else 'stm32f4xx_rcc.c')).read_text(encoding='latin-1')
    if ('@version V3.6.2' if series == 'f1' else '@version V1.8.0') not in spl_rcc:
        raise ValueError('SPL input version differs from provenance')
    if 'FreeRTOS Kernel V10.3.1' not in (a.rtos / 'include/task.h').read_text(encoding='latin-1'):
        raise ValueError('Expected FreeRTOS 10.3.1')
    copy(cmsis / 'Include', stage / 'sdk/cmsis-core')
    copy(device / 'Include', stage / 'sdk/hal-device')
    copy(hal / 'Inc', stage / 'sdk/hal/Inc')
    copy(hal / 'Src', stage / 'sdk/hal/Src')
    copy(spl_device / 'Include', stage / 'sdk/spl-device')
    copy(spl_driver / 'inc', stage / 'sdk/spl/inc')
    copy(spl_driver / 'src', stage / 'sdk/spl/src')
    copy(device / 'Source/Templates' / ('system_' + stem + '.c'), stage / 'sdk/startup' / ('system_' + stem + '.c'))
    if series == 'f1':
        # 原版 Cube SystemInit 不预设时钟，可同时配套 SPL；桥接头仅改变顶层 include。
        write(stage / 'sdk/spl-device/stm32f1xx.h', '#include "stm32f10x.h"\n')
    port = 'ARM_CM3' if series == 'f1' else 'ARM_CM4F'
    for rel in ['include', 'portable/GCC/' + port, 'portable/MemMang/heap_4.c',
                'tasks.c', 'queue.c', 'list.c', 'timers.c', 'event_groups.c', 'stream_buffer.c', 'croutine.c']:
        copy(a.rtos / rel, stage / 'sdk/freertos' / rel)
    for base, title in [(cmsis, 'CMSIS'), (device, 'CMSIS-Device'), (hal, 'HAL'), (spl_driver, 'SPL'), (spl_device, 'SPL-Device')]:
        for f in list(base.glob('*LICENSE*')) + list(base.glob('*License*')) + list(base.glob('*license*')):
            if f.is_file(): copy(f, stage / 'licenses' / (title + '-' + f.name))
    copy(a.cube / 'Package_license.md', stage / 'licenses/Cube-Package.md')
    if series == 'f4':
        copy(a.spl.parent / 'MCD-ST Liberty SW License Agreement V2.pdf', stage / 'licenses/STM32-SPL-Liberty-V2.pdf')
    rtos_header = (a.rtos / 'tasks.c').read_text(encoding='latin-1')
    write(stage / 'licenses/FreeRTOS-MIT.txt', rtos_header[:rtos_header.index('*/') + 2])
    copy(OLD / 'support/runtime.c', stage / 'support/runtime.c')
    copy(RECIPE / 'board_clock.c', stage / 'templates/common/board_clock.c')
    interrupts = (OLD / 'templates/common/interrupts.c').read_text(encoding='utf-8-sig').replace('Cortex-M4F', 'Cortex-M')
    write(stage / 'templates/common/interrupts.c', interrupts)
    app = (OLD / 'templates/common/main.c').read_text(encoding='utf-8-sig').replace('STM32F407ZGT6 · HSE 8 MHz → 168 MHz。', 'STM32 · HSE 8 MHz，型号与最高系统时钟见 board.h。').replace('"heartbeat", 256,', '"heartbeat", configMINIMAL_STACK_SIZE + 32U,')
    write(stage / 'templates/common/main.c', app)
    copy(OLD / 'templates/common/rtos_hooks.c', stage / 'templates/common/rtos_hooks.c')
    enabled = {'HAL', 'HAL_RCC', 'HAL_GPIO', 'HAL_EXTI', 'HAL_DMA', 'HAL_CORTEX', 'HAL_PWR', 'HAL_FLASH', 'HAL_TIM', 'HAL_UART', 'HAL_USART', 'HAL_SPI', 'HAL_I2C', 'HAL_ADC'}
    conf = (hal / 'Inc' / (stem + '_hal_conf_template.h')).read_text(encoding='utf-8-sig')
    conf = re.sub(r'^#define (HAL(?:_[A-Z0-9]+)?)_MODULE_ENABLED.*$', lambda m: m[0] if m[1] in enabled else '/* ' + m[0] + ' */', conf, flags=re.M)
    write(stage / 'templates/hal' / (stem + '_hal_conf.h'), conf.replace('25000000U', '8000000U'))
    # F1 全系列 TIM2；F4 全系列 TIM5，避免在 F401/F411 等不存在 TIM6 的芯片上引用它。
    timebase = (hal / 'Src' / (stem + '_hal_timebase_tim_template.c')).read_text(encoding='utf-8-sig')
    timer = 'TIM2' if series == 'f1' else 'TIM5'
    if series == 'f4': timebase = timebase.replace('TIM6_DAC', 'TIM5').replace('TIM6', 'TIM5')
    timebase = timebase.replace('  HAL_IncTick();', f'  if (htim->Instance == {timer}) HAL_IncTick();')
    write(stage / 'templates/hal/hal_timebase.c', timebase)
    hal_sources = sorted('sdk/hal/Src/' + f.name for f in (hal / 'Src').glob('*.c') if 'template' not in f.name)
    # SPL 驱动用供应商配置头的条件包含；每个文件对未提供的外设自行条件编译。
    if series == 'f1':
        # F1 Keil 包的驱动不依赖 RTE；列出标准模块，assert 保持默认关闭。
        modules = [f.stem for f in (spl_driver / 'src').glob('*.c')]
        spl_conf = '\n'.join('#include "' + m + '.h"' for m in sorted(modules)) + '\n#define assert_param(expr) ((void)0U)\n'
    else:
        spl_conf = (a.spl.parent / 'Project/STM32F4xx_StdPeriph_Templates/stm32f4xx_conf.h').read_text(encoding='utf-8-sig')
    spl_conf_name = ('stm32f10x' if series == 'f1' else stem) + '_conf.h'
    write(stage / 'templates/spl' / spl_conf_name, spl_conf)
    rtos_sources = ['sdk/freertos/' + rel for rel in ['tasks.c', 'queue.c', 'list.c', 'timers.c', 'event_groups.c', 'stream_buffer.c', 'croutine.c', 'portable/GCC/' + port + '/port.c', 'portable/MemMang/heap_4.c']]
    manifest_devices, catalog = [], []
    for d in devices(a.pdsc, series):
        if series == 'f4' and d['id'][6:9] in ('469', '479'):
            d['ccm'] = 65536  # DFP 的 SRAM 只列出连续 320 KiB；CCM 独立于主 SRAM。
        startup = 'startup_' + d['hal'].lower() + '.s'
        copy(device / 'Source/Templates/gcc' / startup, stage / 'sdk/startup' / startup)
        folder = 'templates/devices/' + d['id']
        write(stage / folder / 'board.h', board_header(d, series))
        rtos_conf = (OLD / 'templates/common/FreeRTOSConfig.h').read_text(encoding='utf-8-sig')
        heap = min(32768, d['ram'] // 2)
        small = d['ram'] <= 10240
        rtos_conf = rtos_conf.replace('(32U * 1024U)', str(heap) + 'U')
        if small:
            rtos_conf = rtos_conf.replace('128U', '64U').replace('configUSE_TIMERS                        1', 'configUSE_TIMERS                        0')
        write(stage / folder / 'FreeRTOSConfig.h', rtos_conf)
        linker = (OLD / 'linker/stm32f407zg.ld').read_text(encoding='utf-8-sig')
        linker = linker.replace('LENGTH = 1024K', f"LENGTH = {d['flash'] // 1024}K").replace('LENGTH = 128K', f"LENGTH = {d['ram'] // 1024}K")
        if not d['ccm']:
            linker = '\n'.join(line for line in linker.splitlines() if 'CCM' not in line and '.ccm_noinit' not in line)
        if small:
            linker = linker.replace('_Min_Heap_Size = 0x800;', '_Min_Heap_Size = 0x80;').replace('_Min_Stack_Size = 0x1000;', '_Min_Stack_Size = 0x300;')
        elif d['ram'] <= 32768:
            linker = linker.replace('_Min_Heap_Size = 0x800;', '_Min_Heap_Size = 0x100;').replace('_Min_Stack_Size = 0x1000;', '_Min_Stack_Size = 0x800;')
        write(stage / 'linker' / (d['id'] + '.ld'), linker)
        write(stage / 'debug' / (d['id'] + '.cfg'), openocd(d, series))
        templates = []
        for library in ['hal', 'spl']:
            if library == 'spl' and d['spl'] not in spl_text:
                continue
            for with_rtos in [False, True]:
                tid = library + ('-freertos' if with_rtos else '')
                files = {'include/board.h': folder + '/board.h', 'src/board_clock.c': 'templates/common/board_clock.c', 'src/' + stem + '_it.c': 'templates/common/interrupts.c'}
                if library == 'hal':
                    defines = [d['hal'], 'USE_HAL_DRIVER']
                    includes = ['sdk/hal-device', 'sdk/hal/Inc', 'sdk/hal/Inc/Legacy']
                    sources = hal_sources.copy()
                    files['include/' + stem + '_hal_conf.h'] = 'templates/hal/' + stem + '_hal_conf.h'
                else:
                    defines = list(dict.fromkeys([d['hal'], d['spl'], 'USE_STDPERIPH_DRIVER']))
                    includes = ['sdk/spl-device', 'sdk/spl/inc']
                    sources = ['sdk/spl/src/' + m + '.c' for m in spl_modules(d, series)]
                    files['include/' + spl_conf_name] = 'templates/spl/' + spl_conf_name
                if with_rtos:
                    defines += ['STUDIOX_USE_FREERTOS=1']
                    includes += ['sdk/freertos/include', 'sdk/freertos/portable/GCC/' + port]
                    sources += rtos_sources
                    files['include/FreeRTOSConfig.h'] = folder + '/FreeRTOSConfig.h'
                    files['src/rtos_hooks.c'] = 'templates/common/rtos_hooks.c'
                    if library == 'hal': files['src/hal_timebase.c'] = 'templates/hal/hal_timebase.c'
                title = ('HAL' if library == 'hal' else '标准外设库 SPL') + (' + FreeRTOS' if with_rtos else ' · 裸机')
                templates.append(dict(id=tid, displayName=title, description=f"外部 8 MHz 晶振 → {d['hz'] // 1000000} MHz；{d['flash'] // 1024} KiB Flash / {d['ram'] // 1024} KiB 主 SRAM；内存心跳示例。", entryFile='templates/common/main.c', files=files,
                    build=dict(defines=defines, includeDirectories=includes, sources=sources, compileOptions=[], linkOptions=[])))
        probes = [dict(id=pid, displayName=label, interfaceScript=f'interface/{pid}.cfg', transport='swd', defaultSpeedKhz=2000) for pid, label in [('stlink', 'ST-Link'), ('cmsis-dap', 'CMSIS-DAP'), ('jlink', 'J-Link')]]
        manifest_devices.append(dict(id=d['id'], displayName=f"{d['id']} · {d['hz'] // 1000000} MHz · {d['flash'] // 1024}K / {d['ram'] // 1024}K", architecture='arm',
            flashOrigin=0x08000000, flashBytes=d['flash'], ramOrigin=0x20000000, ramBytes=d['ram'], toolsetId='arm.gnu', toolsetVersion='1.0.0', compilerId='arm-gnu-15.2.rel1',
            cpuFlags=['-mcpu=cortex-m3', '-mthumb'] if series == 'f1' else ['-mcpu=cortex-m4', '-mthumb', '-mfpu=fpv4-sp-d16', '-mfloat-abi=hard'],
            defines=['HSE_VALUE=8000000U', 'USER_VECT_TAB_ADDRESS'], includeDirectories=['sdk/cmsis-core'],
            sources=['sdk/startup/' + startup, 'sdk/startup/system_' + stem + '.c', 'support/runtime.c'], linkerScript='linker/' + d['id'] + '.ld',
            compileOptions=['-Os', '-g3', '-ffunction-sections', '-fdata-sections', '-fno-common', '--specs=nano.specs'],
            linkOptions=['-nostartfiles', '--specs=nano.specs', '--specs=nosys.specs', '-Wl,--gc-sections'], templates=templates,
            openOcd=dict(targetScript='debug/' + d['id'] + '.cfg', probes=probes)))
        catalog.append(d | dict(templates=[t['id'] for t in templates], rtosHeap=heap))
    manifest = dict(formatVersion=1, id='studiox.stm32' + series, version='0.1.0', displayName=f'STM32{series.upper()} 系列 · HAL / SPL / FreeRTOS', vendor='STMicroelectronics / StudioX templates', devices=manifest_devices)
    write(stage / 'manifest.json', json.dumps(manifest, ensure_ascii=False, indent=2))
    write(stage / 'catalog.json', json.dumps(catalog, ensure_ascii=False, indent=2))
    provenance = json.loads(a.provenance.read_text(encoding='utf-8-sig'))
    provenance['pdscSha256'] = digest(a.pdsc)
    # 对实际收入的每个 SDK 文件锁定内容，来源 archive 的 SHA 另见 provenance。
    provenance['sdkFilesSha256'] = {f.relative_to(stage).as_posix(): digest(f) for f in sorted((stage / 'sdk').rglob('*')) if f.is_file()}
    write(stage / 'provenance.json', json.dumps(provenance, ensure_ascii=False, indent=2))
    apply_layout(stage, manifest)
    provenance = json.loads((stage / 'provenance.json').read_text(encoding='utf-8'))
    write(stage / 'manifest.json', json.dumps(manifest, ensure_ascii=False, indent=2))
    copy(RECIPE / 'README.md', stage / 'README.md')
    cli = ROOT / 'src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll'
    if not a.split:
        subprocess.run(['dotnet', str(cli), 'pack', str(stage), str(a.output.resolve())], check=True)
        print(f'{len(catalog)} devices; staging: {stage}; sha256: {digest(a.output)}', flush=True)
        return
    a.output.mkdir(parents=True, exist_ok=True)
    for subfamily in sorted({d['subfamily'] for d in catalog}):
        records = [d for d in catalog if d['subfamily'] == subfamily]
        ids = {d['id'] for d in records}
        members = [d for d in manifest_devices if d['id'] in ids]
        substage = stage.parent / (subfamily.lower() + '-' + uuid.uuid4().hex)
        # 系列包中只留本子系列的目录/链接/调试/启动文件；厂商通用库保留许可和条件头。
        startup_files = {s for d in members for s in d['sources']}
        for f in stage.rglob('*'):
            if not f.is_file(): continue
            rel = f.relative_to(stage).as_posix()
            if rel.startswith('templates/devices/') and rel.split('/')[2] not in ids: continue
            if rel.startswith('system/') and rel.split('/')[1] != 'common' and rel.split('/')[1] not in ids: continue
            if rel.startswith(('debug/', 'linker/')) and f.stem not in ids: continue
            if rel.startswith('sdk/startup/') and rel not in startup_files: continue
            copy(f, substage / rel)
        submanifest = manifest | dict(id='studiox.' + subfamily.lower(), displayName=subfamily + ' · HAL / SPL / FreeRTOS', devices=members)
        subprovenance = provenance | dict(subfamily=subfamily, sdkFilesSha256={k: v for k, v in provenance['sdkFilesSha256'].items() if (substage / k).exists()})
        write(substage / 'manifest.json', json.dumps(submanifest, ensure_ascii=False, indent=2))
        write(substage / 'catalog.json', json.dumps(records, ensure_ascii=False, indent=2))
        write(substage / 'provenance.json', json.dumps(subprovenance, ensure_ascii=False, indent=2))
        output = a.output / ('studiox.' + subfamily.lower() + '-' + VERSION + '.mcupack')
        if output.exists(): raise ValueError('Output already exists: ' + str(output))
        subprocess.run(['dotnet', str(cli), 'pack', str(substage), str(output.resolve())], check=True)
        print(f'{subfamily}: {len(members)} devices, sha256: {digest(output)}', flush=True)


if __name__ == '__main__':
    main()
