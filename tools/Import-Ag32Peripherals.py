"""导入已安装的 AGM 驱动/IP；原厂文件保持字节不变，记录版本与哈希。"""

import argparse
import hashlib
import json
import re
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--sdk', type=Path, required=True)
p.add_argument('--ips', type=Path, required=True)
p.add_argument('--pinout', type=Path, required=True)
a = p.parse_args()
repo = Path(__file__).resolve().parents[1]
dest = repo / 'src/StudioX.Engine/Resources/Ag32/Peripherals'
dest.mkdir(parents=True, exist_ok=True)
files = {}
for source in sorted((a.sdk / 'src').glob('*.c')):
    if source.name in ('interrupt.c', 'tiny-malloc.c'):
        continue  # 中断由原包提供；不替换用户的堆分配器。
    files[source.name] = source
for source in sorted((a.ips / 'ips/analog_ip').glob('*')):
    if source.suffix in ('.h', '.vx', '.asf', '.sdc'):
        files[source.name] = source
metadata = {
    'sdk': json.loads((a.sdk / 'package.json').read_bytes()),
    'ips': json.loads((a.ips / 'package.json').read_bytes()),
    'source': 'https://github.com/agm/platform-agrv',
    'documentation': 'https://www.ag32mcu.com/dev-docs/doc_ag32_analog_code_analysis/',
    'files': {},
    'sdkHeaders': {
        f.name: hashlib.sha256(f.read_bytes()).hexdigest()
        for f in sorted((a.sdk / 'src').glob('*.h'))
    },
    'pinoutSha256': hashlib.sha256(a.pinout.read_bytes()).hexdigest(),
}
for name, source in files.items():
    raw = source.read_bytes()
    (dest / name).write_bytes(raw)
    metadata['files'][name] = hashlib.sha256(raw).hexdigest()
with zipfile.ZipFile(a.pinout) as z:
    ns = {'s': 'http://schemas.openxmlformats.org/spreadsheetml/2006/main'}
    strings = [
        ''.join(e.itertext())
        for e in ET.fromstring(z.read('xl/sharedStrings.xml')).findall('s:si', ns)
    ]
    pins = {}
    for index, count in enumerate([100, 64, 48, 32], 1):
        rows = ET.fromstring(z.read(f'xl/worksheets/sheet{index}.xml')).findall(
            's:sheetData/s:row', ns
        )
        values = []
        for row in rows:
            cells = {}
            for c in row:
                v = c.find('s:v', ns)
                if v is not None:
                    cells[re.sub(r'\d', '', c.attrib['r'])] = (
                        strings[int(v.text)] if c.get('t') == 's' else v.text
                    )
            if cells.get('A', '').isdigit() and re.search('ADC|DAC|CMP', cells.get('C', '')):
                values.append({'pin': int(cells['A']), 'functions': cells['C']})
        if not values:
            raise RuntimeError(f'No analog pins for {count}')
        pins[str(count)] = values
# AG32VH 系列应用指南，第 5–6 页。HyperRAM 占用的焊盘不引出相应模拟通道。
vh_channels = {
    'AGRV2KL64H': {9: 11, 10: 12, 11: 13, 14: 0, 15: 1, 16: 2, 17: 3, 20: 5, 21: 7, 23: 15, 24: 9},
    'AGRV2KL100H': {16: 11, 17: 12, 18: 13, 23: 0, 24: 1, 25: 2, 26: 3, 30: 5, 32: 7, 34: 15, 36: 9},
}
for target, channels in vh_channels.items():
    pins[target] = []
    for pin, channel in channels.items():
        function = ('IO_WKUP_' if channel == 0 else 'IO_') + f'ADC_IN{channel}'
        if channel in (0, 1, 2, 3, 5):
            function += f'_CMP_PA{channel}'
        if channel == 5:
            function += '_DAC1'
        pins[target].append({'pin': pin, 'functions': function})
metadata['vhPinoutSource'] = 'https://www.ag32mcu.com/wp-content/uploads/2025/06/MANUAL_AG32VH_HyperRAM.pdf'
(dest / 'analog-pins.json').write_text(
    json.dumps(pins, ensure_ascii=False, indent=2) + '\n', encoding='utf-8'
)
metadata['files']['analog-pins.json'] = hashlib.sha256(
    (dest / 'analog-pins.json').read_bytes()
).hexdigest()
(dest / 'provenance.json').write_text(
    json.dumps(metadata, ensure_ascii=False, indent=2) + '\n', encoding='utf-8'
)
(dest / 'NOTICE.md').write_text(
    '# AGM 外设与模拟 IP\n\n来源为已安装的 framework-agrv_sdk / framework-agrv_ips 1.0.0，原厂文件不作修改。版本、输入哈希及引脚表来源见 provenance.json。\n\n本机分发未随附独立开源许可证；保留厂商版权，不将本项目许可证扩展到这些文件。原厂中断仍由器件包提供，tiny-malloc 不替换工程的分配器。\n',
    encoding='utf-8',
)
print(json.dumps({'files': len(files), 'pins': pins}, ensure_ascii=False))
