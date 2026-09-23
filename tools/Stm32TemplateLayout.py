"""Keep application entry points separate from generated STM32 system configuration."""
import json
import shutil
from pathlib import Path

RECIPE = Path(__file__).resolve().parents[1] / 'examples/packs/st.stm32-series'
VERSION = '0.1.1'


def apply_layout(stage, manifest):
    if manifest['version'] != '0.1.0':
        raise ValueError('System-layout conversion requires a 0.1.0 source manifest')
    def copy(source, target):
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists() and source.read_bytes() != target.read_bytes():
            raise ValueError('Conflicting system configuration: ' + str(target))
        shutil.copyfile(source, target)

    for source, relative in [('main-bare.c', 'templates/bare/main.c'),
                             ('main-rtos.c', 'templates/rtos/main.c'),
                             ('system_config.h', 'templates/common/system_config.h'),
                             ('system_config.c', 'system/common/system_config.c')]:
        copy(RECIPE / source, stage / relative)
    for device in manifest['devices']:
        for template in device['templates']:
            build = template['build']
            directory = 'system/' + device['id']
            # 包含参数按器件隔离，HAL/SPL 的源码组合继续由原清单明确指定。
            for destination, source in template['files'].items():
                if destination.startswith('src/'):
                    relative = 'system/common/' + Path(destination).name
                    build['sources'].append(relative)
                elif destination.startswith('include/'):
                    relative = directory + '/' + Path(destination).name
                else:
                    raise ValueError('Unexpected template file: ' + destination)
                copy(stage / source, stage / relative)
            build['sources'].append('system/common/system_config.c')
            build['includeDirectories'].append(directory)
            template['files'] = {'include/system_config.h': 'templates/common/system_config.h'}
            template['entryFile'] = 'templates/rtos/main.c' if 'freertos' in template['id'] else 'templates/bare/main.c'
            template['description'] = template['description'].replace('内存心跳示例', '精简 main，系统配置位于 device/system')
    manifest['version'] = VERSION
    provenance_path = stage / 'provenance.json'
    if provenance_path.exists():
        provenance = json.loads(provenance_path.read_text(encoding='utf-8-sig'))
        provenance['templateLayout'] = dict(version=VERSION, systemDirectory='device/system', sdkUnchanged=True)
        provenance_path.write_text(json.dumps(provenance, ensure_ascii=False, indent=2), encoding='utf-8')


if __name__ == '__main__':
    import sys
    stage = Path(sys.argv[1])
    manifest_path = stage / 'manifest.json'
    manifest = json.loads(manifest_path.read_text(encoding='utf-8-sig'))
    apply_layout(stage, manifest)
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
