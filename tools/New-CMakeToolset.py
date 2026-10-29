"""从已校验的本机组件提取独立 CMake/Ninja；保留许可证，不安装全局环境。"""

import argparse, hashlib, json, shutil
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--source', type=Path, required=True)
p.add_argument('--output', type=Path, required=True)
a = p.parse_args()
source, output = a.source.resolve(), a.output.resolve()
if output.exists():
    raise FileExistsError(output)
metadata = json.loads((source / 'toolset.json').read_text(encoding='utf-8-sig'))
files = [
    x
    for x in metadata['sha256']
    if x.startswith(('cmake/', 'ninja/')) or x == 'licenses/Ninja-COPYING.txt'
]
assert (
    files
    and metadata['executables']['cmake'] in files
    and metadata['executables']['ninja'] in files
)
for name in files:
    file = source / name
    assert (
        not file.is_symlink()
        and hashlib.sha256(file.read_bytes()).hexdigest() == metadata['sha256'][name].lower()
    ), name
    target = output / name
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(file, target)
notice = (
    'CMake and Ninja extracted without modification from locally installed '
    + metadata['id']
    + '/'
    + metadata['version']
    + '.\n'
    'Original upstream licenses are retained under cmake and ninja. Compiler binaries are not included.\n'
)
(output / 'PROVENANCE.txt').write_text(notice, encoding='utf8')
manifest = dict(
    formatVersion=1,
    id='studiox.cmake',
    version='4.4.0',
    host='win-x64',
    compilerId='cmake-ninja',
    displayName='CMake / Ninja 工程构建',
    purpose='cmake-build',
    componentVersions={key: metadata['componentVersions'][key] for key in ['cmake', 'ninja']},
    executables={key: metadata['executables'][key] for key in ['cmake', 'ninja']},
    sha256={
        f.relative_to(output).as_posix(): hashlib.sha256(f.read_bytes()).hexdigest()
        for f in sorted(output.rglob('*'))
        if f.is_file()
    },
)
(output / 'toolset.json').write_text(
    json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf8'
)
print(
    json.dumps(
        dict(
            files=len(manifest['sha256']),
            bytes=sum(f.stat().st_size for f in output.rglob('*') if f.is_file()),
        )
    )
)
