"""Repackage locally available, immutable 0.1.0 archives with the 0.1.1 system layout."""
import argparse
import json
import subprocess
import uuid
import zipfile
from pathlib import Path
from Stm32TemplateLayout import VERSION, apply_layout

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('source', type=Path)
p.add_argument('output', type=Path)
a = p.parse_args()
root = Path(__file__).resolve().parents[1]
archives = sorted(a.source.glob('*.mcupack')) if a.source.is_dir() else [a.source]
if not archives:
    raise ValueError('No source archives')
a.output.mkdir(parents=True, exist_ok=True)
for archive in archives:
    with zipfile.ZipFile(archive) as z:
        manifest = json.loads(z.read('manifest.json').decode('utf-8-sig'))
        if not manifest['id'].startswith('studiox.stm32f') or manifest['version'] != '0.1.0':
            raise ValueError('Expected a StudioX STM32 0.1.0 pack: ' + str(archive))
        output = a.output / (manifest['id'] + '-' + VERSION + '.mcupack')
        if output.exists():
            raise ValueError('Output already exists: ' + str(output))
        stage = root / '.artifacts' / ('system-layout-' + uuid.uuid4().hex)
        stage.mkdir(parents=True)
        for name in z.namelist():
            if '\\' in name or not (stage / name).resolve().is_relative_to(stage.resolve()):
                raise ValueError('Unsafe archive path: ' + name)
        z.extractall(stage)
    (stage / 'files.sha256.json').unlink()
    apply_layout(stage, manifest)
    (stage / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
    subprocess.run(['dotnet', str(root / 'src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll'), 'pack', str(stage), str(output.resolve())], check=True)
    print(output.name, flush=True)
