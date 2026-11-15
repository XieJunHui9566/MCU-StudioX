"""Audit preserved black-box outputs. Never execute a generated firmware."""
import argparse
import collections
from pathlib import Path
import re
from run import image, listing, write


def instructions(text):
    for line in text.splitlines():
        match = re.match(r'^([0-9A-F]{6})\s+([0-9A-F]+)\s+(?:[A-Z]\s+)?([A-Z][A-Z0-9]*)\s*(.*)$', line)
        if match:
            yield dict(address=int(match[1], 16), bytes=bytes.fromhex(match[2]),
                       op=match[3], operands=match[4].split(';')[0].strip())


def count(rows):
    counts = collections.Counter(x['op'] for x in rows)
    stack = {'byte': 0, 'word': 0, 'dword': 0, 'payloadBytes': 0}
    for row in rows:
        if row['op'] != 'MOV' or '@DR60' not in row['operands']:
            continue
        operand = next(x.strip() for x in row['operands'].split(',') if '@DR60' not in x)
        width = 4 if operand.startswith('DR') else 2 if operand.startswith('WR') else 1
        stack[{1: 'byte', 2: 'word', 4: 'dword'}[width]] += 1
        stack['payloadBytes'] += width
    return dict(codeBytes=sum(len(x['bytes']) for x in rows), instructions=len(rows), opcodes=dict(counts),
                stackAccess=stack, abiConversion=None, allocatorSpill=None,
                commonCallsites=sum(x['op'] in ['ECALL', 'LCALL', 'ACALL'] and '?L?COM' in x['operands'] for x in rows))


def map_segments(path):
    result = []
    for line in path.read_text(errors='replace').splitlines():
        match = re.match(r'^([0-9A-F]+)H\s+([0-9A-F]+)H\s+([0-9A-F]+)H\s+\S+\s+\S+\s+(CODE|ECODE|HCONST)\s+(\S+)', line)
        if match:
            result.append(dict(name=match[5], address=int(match[1], 16), codeBytes=int(match[3], 16), area=match[4]))
    return result


def main():
    import json
    parser = argparse.ArgumentParser()
    parser.add_argument('--research', type=Path, required=True)
    args = parser.parse_args()
    root = args.research
    read = lambda p: json.loads(p.read_text(encoding='utf-8-sig'))
    sources = {'calls': 'calls-c251-timer-bytes', 'mcu': 'mcu-c251', 'protocol': 'protocol-c251-timer-bytes'}
    data = dict(suites={}, compilerChanged=False, hardwareExecuted=False, cycles=None,
                interpretation='Offline size/code-generation experiment; new optimized HEX correctness has not been board-validated.')
    for suite, reference_name in sources.items():
        folder = root / ('fixed-' + suite)
        results = read(folder / 'results.json')
        reference = root.parent / 'comparison-r89-20261026/builds' / reference_name
        base_image = image(folder / 'baseline/pass-0/firmware.hex')
        assert base_image == image(reference / 'firmware.hex')
        rows = {}
        for variant, result in results.items():
            assert result['compiled'] and result['converged'], (suite, variant)
            final = folder / variant / ('pass-' + str(len(result['passes']) - 1))
            memory = image(final / 'firmware.hex')
            assert all(0xFF0000 <= x < 0x1000000 for x in memory)
            # Same C source and headers in every iteration, not merely the final one.
            for pass_index in range(len(result['passes'])):
                for source in reference.iterdir():
                    if source.suffix.lower() in ['.c', '.h']:
                        assert source.read_bytes() == (folder / variant / ('pass-' + str(pass_index)) / source.name).read_bytes()
            unit_rows = {u: listing(final / (u + '.lst')) for u in result['passes'][-1]['listing']}
            if (final / 'FIRMWARE.COD').exists():
                parsed = list(instructions((final / 'FIRMWARE.COD').read_text(errors='replace')))
                seen = {}
                for row in parsed:
                    assert row['address'] not in seen, 'Duplicate final instruction address'
                    seen[row['address']] = row
                    assert row['bytes'] == bytes(memory[row['address'] + i] for i in range(len(row['bytes']))), (suite, variant, row)
                provenance = 'Final linker FIRMWARE.COD; each instruction byte verified against HEX; excludes startup/vectors/constants.'
            else:
                parsed = [x for unit in unit_rows for x in instructions((final / (unit + '.lst')).read_text(errors='replace'))]
                provenance = 'Compiler LST instruction counts; no linker packing; excludes startup/vectors/constants.'
            segments = map_segments(final / 'FIRMWARE.MAP')
            assert sum(x['codeBytes'] for x in segments) == len(memory)
            rows[variant] = dict(codeBytes=len(memory), deltaBytes=len(memory) - len(base_image),
                                 reductionPercent=100*(1-len(memory)/len(base_image)),
                                 passSizes=[x['codeBytes'] for x in result['passes']],
                                 passCount=len(result['passes']),
                                 sameImageAsBaseline=memory == base_image, stats=count(parsed),
                                 statsScope=provenance, units=unit_rows, segments=segments,
                                 finalPath=str(final), hexSha256=result['passes'][-1]['hexSha256'])
        assert rows['size7']['sameImageAsBaseline']
        changes = []
        for unit, metrics in rows['baseline']['units'].items():
            for function, before in metrics['functions'].items():
                after = rows['grc']['units'][unit]['functions'][function]
                if before != after:
                    changes.append(dict(unit=unit, function=function, before=before, after=after))
        data['suites'][suite] = dict(referencePath=str(reference), baselineImageReproduced=True,
                                     variants=rows, grcFunctionChanges=changes)
    calls = data['suites']['calls']
    assert len(calls['grcFunctionChanges']) == 10
    assert all(x['unit'] != 'main' for x in calls['grcFunctionChanges'])
    probes = read(root / 'probes/results.json')
    corrected = read(root / 'probes-large-corrected/results.json')
    data['registerProbes'] = {k: v for k, v in {**probes, **corrected}.items() if not k.startswith('call-widths')}
    assert all(x['compiled'] and x['converged'] for x in data['registerProbes'].values())
    data['callWidths'] = {k: v for k, v in probes.items() if k.startswith('call-widths')}
    data['nearBoundary'] = read(root / 'near-boundary-corrected/results.json')
    assert data['nearBoundary']['same-segment']['exitCode'] == 0
    assert 'IMPROPER FIXUP' in data['nearBoundary']['cross-segment']['output']
    data['setupFailuresRetained'] = [
        'probes/large-baseline and large-grc: runner initially used incorrect library name C2LSH; corrected to installed C2SLH, no compiler defect.',
        'near-boundary: initial main probe omitted required startup symbol; corrected standalone link harness, no compiler defect.']
    write(root / 'analysis.json', data)
    for suite, item in data['suites'].items():
        print(suite, [(k, v['codeBytes']) for k, v in item['variants'].items()])
    print('All three baselines, source identity, REGFILE convergence and packed COD instruction bytes verified.')


if __name__ == '__main__':
    main()
