"""Write a human-readable catalog and checksums for the separately delivered packs."""
import hashlib
import json
import sys
import zipfile
from pathlib import Path

root = Path(sys.argv[1])
entries = []
for path in sorted(root.glob('*.mcupack')):
    with zipfile.ZipFile(path) as archive:
        manifest = json.loads(archive.read('manifest.json').decode('utf-8-sig'))
        catalog = json.loads(archive.read('catalog.json').decode('utf-8-sig'))
        family = {d['subfamily'] for d in catalog}
        assert len(family) == 1, f'Mixed subfamilies: {path}'
        assert manifest['id'] == 'studiox.' + next(iter(family)).lower()
        assert len(manifest['devices']) == len(catalog)
        assert all(len(d['templates']) == 4 for d in manifest['devices'])
        entries.append(dict(file=path.name, id=manifest['id'], version=manifest['version'],
                            subfamily=next(iter(family)), devices=[d['id'] for d in catalog],
                            systemClockMHz=sorted({d['hz'] // 1000000 for d in catalog}),
                            bytes=path.stat().st_size, sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
assert len(entries) == 23 and sum(len(e['devices']) for e in entries) == 244
(root / 'index.json').write_text(json.dumps(entries, ensure_ascii=False, indent=2), encoding='utf-8')
text = '''# STM32 独立子系列包

23 个独立 .mcupack，244 个基础型号。按需要导入对应子系列包，例如 STM32F103C8T6 选择 **STM32F103** 包中的 **STM32F103C8**。
每包均提供 HAL、SPL、HAL + FreeRTOS、SPL + FreeRTOS；外部 8 MHz 晶振，按型号配置最高系统频率。
编译器、OpenOCD 和构建工具由 IDE 提供，包内不包含工具二进制。ST-Link / CMSIS-DAP / J-Link 均提供 SWD 配置。

| 子系列 | 基础型号数 | 默认系统时钟 | 芯片包 |
|---|---:|---:|---|
'''
for e in entries:
    text += f"| {e['subfamily']} | {len(e['devices'])} | {','.join(map(str, e['systemClockMHz']))} MHz | [{e['file']}]({e['file']}) |\n"
text += '\n完整型号与 SHA-256 见 [index.json](index.json)。包内 `provenance.json` 记录 SDK 版本、下载来源和校验值，`licenses/` 保留第三方许可。\n\n注意：编译和离线配置检查通过，不等于这些型号均已通过实板烧录验证。模板假设 3.3 V 供电，不预设板上 LED/串口接线；100/180 MHz 的 F4 使用 USB/SDIO 等外设前需另行配置对应外设时钟。\n'
(root / 'README.md').write_text(text, encoding='utf-8')
print(f'Indexed {len(entries)} separate packs, 244 devices.')
