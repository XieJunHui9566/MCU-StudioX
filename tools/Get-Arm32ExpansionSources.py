"""Explicit maintainer download of official CMSIS packs; save version, URL and SHA-256."""

import argparse
import concurrent.futures
import hashlib
import io
import json
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

SELECTED = {
    'Keil': [
        'STM32F0xx_DFP',
        'STM32F2xx_DFP',
        'STM32F3xx_DFP',
        'STM32F7xx_DFP',
        'STM32G0xx_DFP',
        'STM32G4xx_DFP',
        'STM32L0xx_DFP',
        'STM32L1xx_DFP',
        'STM32L4xx_DFP',
        'STM32H7xx_DFP',
        'STM32C0xx_DFP',
        'STM32U0xx_DFP',
        'LPC1700_DFP',
        'LPC1100_DFP',
    ],
    'NordicSemiconductor': ['nRF_DeviceFamilyPack'],
    'Microchip': ['SAMD21_DFP', 'SAMD51_DFP'],
    'NXP': [
        'LPC810_DFP',
        'LPC812_DFP',
        'LPC824_DFP',
        'LPC845_DFP',
        'MKL25Z4_DFP',
        'MKL26Z4_DFP',
        'MK64F12_DFP',
    ],
    'HDSC': ['HC32L110', 'HC32F003', 'HC32F005', 'HC32F030', 'HC32F072', 'HC32L130', 'HC32L136'],
    'MindMotion': ['MM32F003_DFP', 'MM32F031_DFP', 'MM32F103x8xB_DFP', 'MM32F3270_DFP'],
    'Geehy': ['APM32F0xx_DFP', 'APM32F1xx_DFP', 'APM32F4xx_DFP'],
    'NSING': [
        'N32G003_DFP',
        'N32G031_DFP',
        'N32G43x_DFP',
        'N32G45x_DFP',
        'N32G430_DFP',
        'N32L40x_DFP',
    ],
    'Nuvoton': ['NuMicroM0_DFP', 'NuMicroM4_DFP'],
}


def download(record, root):
    identity = record['vendor'] + '.' + record['name'] + '.' + record['version']
    extension = '.atpack' if record['vendor'] == 'Microchip' else '.pack'
    filename = identity + extension
    url = record['url'].replace('http://', 'https://').rstrip('/') + '/' + filename
    path = root / filename
    result = dict(record, file=filename, requestedUrl=url)
    try:
        if not path.exists():
            request = urllib.request.Request(
                url, headers={'User-Agent': 'MCU-StudioX-Pack-Maintainer/1.0'}
            )
            with urllib.request.urlopen(request, timeout=50) as response:
                result['resolvedUrl'] = response.url
                content = response.read(300 * 1024 * 1024)
            with zipfile.ZipFile(io.BytesIO(content)) as archive:
                if archive.testzip():
                    raise ValueError('Downloaded pack failed CRC verification')
            path.write_bytes(content)
        with zipfile.ZipFile(path) as archive:
            if archive.testzip():
                raise ValueError('Cached pack failed CRC verification')
        result.update(
            sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
            bytes=path.stat().st_size,
            status='downloaded',
        )
    except Exception as error:
        result.update(status='failed', error=repr(error))
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--index', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    selected = {}
    for node in ET.parse(args.index).findall('./pindex/pdsc'):
        if node.get('name') in SELECTED.get(node.get('vendor'), []):
            selected[node.get('vendor') + '.' + node.get('name')] = dict(node.attrib)
    records = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures = [pool.submit(download, record, args.output) for record in selected.values()]
        for future in concurrent.futures.as_completed(futures):
            record = future.result()
            records.append(record)
            print(
                record['status'],
                record['vendor'] + '.' + record['name'],
                record.get('bytes', record.get('error')),
                flush=True,
            )
            (args.output / 'downloads.json').write_text(
                json.dumps(sorted(records, key=lambda x: x['file']), ensure_ascii=False, indent=2),
                encoding='utf-8',
            )
    print(
        'Downloaded',
        sum(r['status'] == 'downloaded' for r in records),
        'of',
        len(records),
        flush=True,
    )


if __name__ == '__main__':
    main()
