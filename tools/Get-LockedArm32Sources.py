"""Fetch reviewed ARM32 sources by lock-file hashes, never resolve a newer version."""

import argparse, concurrent.futures, hashlib, io, json, urllib.request, zipfile
from pathlib import Path


def digest(data):
    return hashlib.sha256(data).hexdigest()


def fetch(url, expected, path):
    if path.exists():
        data = path.read_bytes()
        if digest(data) == expected:
            return data
        raise ValueError('Cached source differs from the lock: ' + str(path))
    for attempt in range(3):
        try:
            request = urllib.request.Request(
                url, headers={'User-Agent': 'MCU-StudioX-Pack-Maintainer/1.0'}
            )
            with urllib.request.urlopen(request, timeout=30) as response:
                blocks = []
                while block := response.read(32768):
                    blocks.append(block)
                data = b''.join(blocks)
            if digest(data) != expected:
                raise ValueError('Source hash mismatch: ' + url)
            with zipfile.ZipFile(io.BytesIO(data)) as archive:
                if archive.testzip():
                    raise ValueError('Archive CRC failure: ' + url)
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(data)
            return data
        except Exception:
            if attempt == 2:
                raise


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument(
        '--lock',
        type=Path,
        default=Path(__file__).resolve().parents[1]
        / 'examples/packs/arm32-expansion/sources.lock.json',
    )
    p.add_argument('--output', type=Path, required=True)
    a = p.parse_args()
    lock = json.loads(a.lock.read_text(encoding='utf-8'))
    a.output.mkdir(parents=True, exist_ok=True)
    # 官方 AT32 安装包只是 ZIP 容器，提取 DFP 数据，不执行安装程序。
    bundles = {}
    for r in lock['dfp']:
        if 'bundleSha256' in r and r['bundleSha256'] not in bundles:
            bundles[r['bundleSha256']] = fetch(
                r['requestedUrl'],
                r['bundleSha256'],
                a.output / 'bundles' / (r['bundleSha256'] + '.zip'),
            )

    def one(r, folder):
        dest = a.output / folder / r['file']
        if 'bundleSha256' in r:
            with zipfile.ZipFile(io.BytesIO(bundles[r['bundleSha256']])) as z:
                data = z.read(r['bundleMember'])
            if digest(data) != r['sha256']:
                raise ValueError('Bundle member hash differs: ' + r['file'])
            if dest.exists() and dest.read_bytes() != data:
                raise ValueError('Existing source changed: ' + str(dest))
            dest.parent.mkdir(parents=True, exist_ok=True)
            dest.write_bytes(data)
        else:
            fetch(r.get('requestedUrl', r.get('url')), r['sha256'], dest)
        print('Verified ' + r['file'], flush=True)

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = [pool.submit(one, r, 'downloads') for r in lock['dfp']] + [
            pool.submit(one, r, 'st-cmsis') for r in lock['stCmsis']
        ]
        for future in concurrent.futures.as_completed(futures):
            future.result()
    for path, rows in [
        ('downloads/downloads.json', lock['dfp']),
        ('st-cmsis/sources.json', lock['stCmsis']),
    ]:
        (a.output / path).write_text(
            json.dumps(rows, ensure_ascii=False, indent=2), encoding='utf-8'
        )


if __name__ == '__main__':
    main()
