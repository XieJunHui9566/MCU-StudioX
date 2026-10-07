"""Upgrade verified F1/F4 format-1 packs with separate vendor LL templates."""
import argparse
import copy
import hashlib
import json
import re
import shutil
import zipfile
from pathlib import Path

VERSION = '0.1.3'


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def upgrade(source, stage, output):
    manifest = json.loads((source / 'manifest.json').read_text(encoding='utf-8-sig'))
    assert manifest['version'] == '0.1.1'
    assert all({t['id'] for t in d['templates']} == {'hal', 'hal-freertos', 'spl', 'spl-freertos'} for d in manifest['devices'])
    shutil.copytree(source, stage)
    prefix = 'stm32f1xx' if manifest['devices'][0]['id'].startswith('STM32F1') else 'stm32f4xx'
    for part in ('Inc', 'Src'):
        target = stage / 'sdk/ll' / part
        target.mkdir(parents=True)
        for file in (stage / 'sdk/hal' / part).glob(prefix + '_ll_*'):
            if not re.search(r'#include\s+"[^"\n]*_hal[^"\n]*"', file.read_text(encoding='utf-8-sig')):
                shutil.copyfile(file, target / file.name)
    # USB/FMC/FSMC 等 HAL 内部底层也命名为 LL，但依赖 HAL；独立 LL 不能混入这些驱动。
    ll_sources = sorted('sdk/ll/Src/' + f.name for f in (stage / 'sdk/ll/Src').glob('*.c') if not re.search(r'#include\s+"[^"\n]*_hal[^"\n]*"', f.read_text(encoding='utf-8-sig')))
    clock = stage / 'system/common/board_clock.c'
    clock_text = clock.read_text(encoding='utf-8-sig')
    clock_text = clock_text.replace('#else\n    NVIC_PriorityGroupConfig(NVIC_PriorityGroup_4);', '#else\n#ifdef USE_FULL_LL_DRIVER\n    /* CMSIS 分组 3：四位抢占优先级，供独立 LL 与 FreeRTOS 使用。 */\n    NVIC_SetPriorityGrouping(3U);\n#else\n    NVIC_PriorityGroupConfig(NVIC_PriorityGroup_4);\n#endif')
    assert 'NVIC_SetPriorityGrouping(3U)' in clock_text
    clock.write_text(clock_text, encoding='utf-8')
    for device in manifest['devices']:
        templates = {t['id']: t for t in device['templates']}
        macro = next(d for d in templates['hal']['build']['defines'] if d.startswith('STM32'))
        board = stage / 'system' / device['id'] / 'board.h'
        text = board.read_text(encoding='utf-8-sig')
        marker = '#else\n#include "stm32f10x.h"' if prefix == 'stm32f1xx' else '#else\n#include "stm32f4xx.h"'
        assert marker in text, board
        text = text.replace(marker, '#elif defined(USE_FULL_LL_DRIVER)\n#include "' + prefix + '.h"\n' + marker)
        board.write_text(text, encoding='utf-8')
        for rtos in (False, True):
            suffix = '-freertos' if rtos else ''
            template = copy.deepcopy(templates['spl' + suffix])
            template['id'] = 'll' + suffix
            template['displayName'] = 'LL + FreeRTOS' if rtos else 'LL · 裸机'
            build = template['build']
            build['defines'] = [v for v in build['defines'] if not v.startswith('STM32') and v != 'USE_STDPERIPH_DRIVER'] + [macro, 'USE_FULL_LL_DRIVER']
            build['includeDirectories'] = [v for v in build['includeDirectories'] if not v.startswith('sdk/spl')] + ['sdk/hal-device', 'sdk/ll/Inc']
            build['sources'] = [v for v in build['sources'] if not v.startswith('sdk/spl/')] + ll_sources
            device['templates'].append(template)
    manifest['version'] = VERSION
    manifest['displayName'] = manifest['displayName'].replace('HAL / SPL / FreeRTOS', 'HAL / SPL / LL / FreeRTOS')
    manifest['stm32LibrarySupport'] = {'policyVersion': 1, 'libraries': ['hal', 'spl', 'll'], 'evidence': 'provenance.json'}
    write_json(stage / 'manifest.json', manifest)
    provenance_path = stage / 'provenance.json'
    provenance = json.loads(provenance_path.read_text(encoding='utf-8-sig'))
    provenance['libraryTemplates'] = {'version': VERSION, 'libraries': ['hal', 'spl', 'll'], 'llSource': 'existing STM32Cube HAL/LL SDK, copied byte-for-byte', 'sourceManifestSha256': hashlib.sha256((source / 'manifest.json').read_bytes()).hexdigest(), 'halInternalLlExcluded': ['usb', 'fmc', 'fsmc', 'sdmmc']}
    write_json(provenance_path, provenance)
    with (stage / 'README.md').open('a', encoding='utf-8') as f:
        f.write('\n## 完整外设库模板\n\n0.1.3 提供 HAL、SPL、LL 及各自的 FreeRTOS 模板。SDK 目录与编译参数按框架隔离；LL 来自同版本 STM32Cube，保留原许可证。USB/FMC/FSMC/SDMMC 的 *_ll 文件依赖 HAL 类型，随 HAL SDK 提供，不混入独立 LL 模板。离线编译不代表实板验收。\n')
    archive = output / (manifest['id'] + '-' + VERSION + '.mcupack')
    files = sorted(p for p in stage.rglob('*') if p.is_file() and p.name != 'files.sha256.json')
    hashes = {}
    with zipfile.ZipFile(archive, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for file in files:
            name = file.relative_to(stage).as_posix()
            data = file.read_bytes()
            hashes[name] = hashlib.sha256(data).hexdigest()
            z.writestr(zipfile.ZipInfo(name, (2000, 1, 1, 0, 0, 0)), data, compress_type=zipfile.ZIP_DEFLATED)
        z.writestr(zipfile.ZipInfo('files.sha256.json', (2000, 1, 1, 0, 0, 0)), json.dumps(hashes, indent=2), compress_type=zipfile.ZIP_DEFLATED)
    return {'id': manifest['id'], 'version': VERSION, 'sha256': hashlib.sha256(archive.read_bytes()).hexdigest(), 'size': archive.stat().st_size, 'devices': len(manifest['devices']), 'templatesPerDevice': 6}


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('installed')
    parser.add_argument('staging')
    parser.add_argument('output')
    args = parser.parse_args()
    staging, output = Path(args.staging), Path(args.output)
    staging.mkdir(parents=True, exist_ok=False)
    output.mkdir(parents=True, exist_ok=False)
    results = []
    for pack in sorted(Path(args.installed).glob('studiox.stm32*')):
        source = pack / '0.1.1/payload'
        if source.exists() and len(pack.name) == len('studiox.stm32f103'):
            results.append(upgrade(source, staging / pack.name, output))
    assert len(results) == 23
    write_json(output / 'build-index.json', results)
    print(f'Created {len(results)} complete packs, {sum(r["devices"] for r in results)} devices')
