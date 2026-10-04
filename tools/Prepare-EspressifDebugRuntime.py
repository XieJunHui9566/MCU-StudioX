"""将本机已有官方 GDB 纳入新的受管理 IDF 工具目录；不联网、不覆盖来源。"""

import argparse
import hashlib
import json
import os
import shutil
import subprocess
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--source', type=Path, required=True)
p.add_argument('--tools-root', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
source, tools, output = a.source.resolve(), a.tools_root.resolve(), a.output.resolve()
if (
    output.exists()
    or output.is_relative_to(source)
    or source.is_relative_to(output)
    or output.is_relative_to(tools)
):
    raise ValueError('Use a new output directory outside source tool directories')
manifest = json.loads((source / 'toolset.json').read_text(encoding='utf-8-sig'))
if (manifest['id'], manifest['version'], manifest['purpose']) != (
    'espressif.idf',
    '5.5.4',
    'esp-idf',
):
    raise ValueError('This recipe is pinned to IDF 5.5.4')
spec = json.loads(
    (source / manifest['resourceDirectories']['idf'] / 'tools/tools.json').read_text()
)
output.mkdir(parents=True)
for relative, expected in manifest['sha256'].items():
    item = source / relative
    if item.is_symlink() or not item.resolve().is_relative_to(source):
        raise ValueError('Linked or out-of-root source')
    with item.open('rb') as f:
        if hashlib.file_digest(f, 'sha256').hexdigest().lower() != expected.lower():
            raise ValueError('Source hash mismatch: ' + relative)
    dest = output / relative
    if not dest.resolve().is_relative_to(output):
        raise ValueError('Output path escapes root')
    dest.parent.mkdir(parents=True, exist_ok=True)
    try:
        os.link(item, dest)
    except OSError:
        shutil.copy2(item, dest)
version = '16.3_20250913'
for folder, package, prefix in [
    ('xtensa-gdb', 'xtensa-esp-elf-gdb', 'xtensa-esp32'),
    ('riscv-gdb', 'riscv32-esp-elf-gdb', 'riscv32-esp'),
]:
    entry = next(t for t in spec['tools'] if t['name'] == package)
    if next(v['name'] for v in entry['versions'] if v['status'] == 'recommended') != version:
        raise ValueError('GDB version differs from SDK recommended identity')
    src = tools / package / version / package
    for directory, children, files in os.walk(src):
        children[:] = [n for n in children if n != '__pycache__']
        for name in files:
            item = Path(directory) / name
            if item.is_symlink() or not item.resolve().is_relative_to(src):
                raise ValueError('Linked GDB source')
            rel = item.relative_to(src)
            dest = output / folder / rel
            dest.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, dest)
            with dest.open('rb') as f:
                manifest['sha256'][dest.relative_to(output).as_posix()] = hashlib.file_digest(
                    f, 'sha256'
                ).hexdigest()
    exe = output / folder / 'bin' / (prefix + '-elf-gdb.exe')
    env = dict(os.environ, PYTHONDONTWRITEBYTECODE='1', PYTHONNOUSERSITE='1')
    for key in ['PYTHONHOME', 'PYTHONPATH', 'PYTHONPYCACHEPREFIX']:
        env.pop(key, None)
    result = subprocess.run(
        [str(exe), '--nx', '--version'],
        cwd=output,
        env=env,
        capture_output=True,
        text=True,
        timeout=30,
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    if result.returncode or '16.3' not in result.stdout:
        raise ValueError('GDB version check failed: ' + result.stdout + result.stderr)
manifest['executables'].update(
    {
        'gdb-esp32': 'xtensa-gdb/bin/xtensa-esp32-elf-gdb.exe',
        'gdb-esp32s3': 'xtensa-gdb/bin/xtensa-esp32s3-elf-gdb.exe',
        'gdb-riscv': 'riscv-gdb/bin/riscv32-esp-elf-gdb.exe',
    }
)
manifest['componentVersions']['gdb'] = version
(output / 'toolset.json').write_text(json.dumps(manifest, indent=2), encoding='utf-8')
print('Prepared managed GDB roles with retained vendor files and SHA-256 index:', output)
