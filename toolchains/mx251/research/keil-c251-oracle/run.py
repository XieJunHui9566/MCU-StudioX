"""Offline C251 experiments; no hardware access or installed toolchain mutation.

Supply the existing Keil installation and frozen calls sources explicitly.
Every pass retains its own sources, compiler/linker logs, REGFILE and image.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + '\n', encoding='utf8')


def image(path):
    memory, base = {}, 0
    for line in path.read_text().splitlines():
        record = bytes.fromhex(line[1:])
        assert len(record) == record[0] + 5 and sum(record) % 256 == 0
        if record[3] == 4:
            base = int.from_bytes(record[4:6], 'big') << 16
        elif record[3] == 2:
            base = int.from_bytes(record[4:6], 'big') << 4
        elif record[3] == 0:
            start = base + int.from_bytes(record[1:3], 'big')
            for offset, value in enumerate(record[4:-1]):
                assert start + offset not in memory
                memory[start + offset] = value
    assert memory
    return memory


def listing(path):
    """Compiler listing metrics, explicitly before linker code packing.

    Stack byte accesses mean explicit @DR60 load/stores whose data operand
    is byte wide; separate word/dword and payload counts avoid ambiguity.
    ABI conversion and allocator spills cannot be inferred from MOV alone.
    """
    text = path.read_text(errors='replace')
    functions = {}
    for name, body in re.findall(r';\s*FUNCTION (\S+) \(BEGIN\)(.*?);\s*FUNCTION \S+ \(END\)', text, re.S):
        rows = []
        for line in body.splitlines():
            match = re.match(r'^([0-9A-F]{6})\s+([0-9A-F]+)\s+(?:[A-Z]\s+)?([A-Z][A-Z0-9]*)\s*(.*)$', line)
            if match:
                rows.append((match[3], match[4].split(';')[0].strip(), len(match[2]) // 2))
        counts = {op: sum(x[0] == op for x in rows) for op in ['MOV', 'PUSH', 'POP', 'ECALL', 'LCALL', 'ACALL', 'ERET', 'RET']}
        stack = {'byte': 0, 'word': 0, 'dword': 0, 'payloadBytes': 0}
        for op, operands, _ in rows:
            if op != 'MOV' or '@DR60' not in operands:
                continue
            other = next((x.strip() for x in operands.split(',') if '@DR60' not in x), '')
            width = 4 if other.startswith('DR') else 2 if other.startswith('WR') else 1
            stack[{1: 'byte', 2: 'word', 4: 'dword'}[width]] += 1
            stack['payloadBytes'] += width
        functions[name] = dict(codeBytes=sum(x[2] for x in rows), instructions=len(rows), **counts,
                               stackAccess=stack, abiConversion=None, allocatorSpill=None)
    areas = {k: int(v) for k, v in re.findall(r'^\s*(code|ecode|const|hconst) size\s*=\s*(\d+)', text, re.M)}
    return dict(areas=areas, functions=functions,
                disassemblyAvailable=bool(functions),
                note='Compiler listing only; OBJECTADVANCED requires final FIRMWARE.COD for instruction counts.')


def normalized_reg(path):
    # Only strip blank lines. Do not guess away fields that may affect codegen.
    return '\n'.join(x.rstrip() for x in path.read_text(errors='replace').splitlines() if x.strip())


def build(keil, source, destination, units, startup, level=7, packing=False,
          feedback=False, model='SMALL', emphasis='SPEED', rom='HUGE', maximum_passes=6):
    destination.mkdir(parents=True, exist_ok=False)
    history, previous_reg, previous_image = [], None, None
    try:
        for iteration in range(maximum_passes if feedback else 1):
            folder = destination / ('pass-' + str(iteration))
            folder.mkdir()
            for src in source.iterdir():
                if src.suffix.lower() in ['.c', '.h']:
                    shutil.copyfile(src, folder / src.name)
            shutil.copyfile(startup, folder / 'startup.a51')
            if previous_reg:
                shutil.copyfile(previous_reg, folder / 'usage.reg')
                shutil.copyfile(previous_reg, folder / 'input.reg')
            commands = []

            def run(name, args):
                args = list(map(str, args))
                process = subprocess.run(args, cwd=folder, capture_output=True, timeout=90)
                (folder / (name + '.stdout.log')).write_bytes(process.stdout)
                (folder / (name + '.stderr.log')).write_bytes(process.stderr)
                commands.append(dict(name=name, args=args, exitCode=process.returncode))
                write(folder / 'commands.json', commands)
                output = process.stdout + process.stderr
                if process.returncode and not (process.returncode == 1 and b'0 ERROR(S)' in output):
                    raise RuntimeError(name + ': exit ' + str(process.returncode))

            for unit in units:
                options = [model, 'ROM(' + rom + ')', f'OPTIMIZE({level},{emphasis})', 'CODE', 'INCDIR(' + str(keil / 'INC') + ')']
                if packing:
                    options.append('OBJECTADVANCED')
                if feedback:
                    options.append('REGFILE(usage.reg)')
                run(unit, [keil / 'BIN/C251.exe', unit + '.c', *options])
            run('startup', [keil / 'BIN/A251.exe', 'startup.a51'])
            libraries = {'SMALL': 'C2SSH.LIB', 'LARGE': 'C2SLH.LIB'}
            inputs = ','.join([x + '.obj' for x in units] + ['startup.obj', str(keil / 'LIB' / libraries[model])])
            link_options = ['NODEFAULTLIBRARY', 'CLASSES(CODE(C:0-C:0xFFFF),ECODE(0xFF0000-0xFFFFFF),HCONST(0xFF0000-0xFFFFFF),XDATA(X:0-X:0x6FFF),EDATA(0x100-0x3FF))']
            if feedback:
                link_options.append('REGFILE(usage.reg)')
            if packing:
                link_options.append('CODE')
            run('link', [keil / 'BIN/L251.exe', inputs, 'TO', 'firmware', *link_options])
            run('hex', [keil / 'BIN/OH251.exe', 'firmware', 'HEXFILE(firmware.hex)'])
            memory = image(folder / 'firmware.hex')
            regs = normalized_reg(folder / 'usage.reg') if feedback else None
            row = dict(passIndex=iteration, codeBytes=len(memory), spanBytes=max(memory)-min(memory)+1,
                       minAddress=min(memory), maxAddress=max(memory), hexSha256=sha(folder / 'firmware.hex'),
                       listing={u: listing(folder / (u + '.lst')) for u in units},
                       sameImageAsPrevious=memory == previous_image,
                       sameRegfileAsPrevious=bool(previous_reg) and regs == normalized_reg(previous_reg),
                       regfileSha256=sha(folder / 'usage.reg') if feedback else None)
            history.append(row)
            write(folder / 'metrics.json', row)
            if feedback and row['sameImageAsPrevious'] and row['sameRegfileAsPrevious']:
                break
            previous_image = memory
            previous_reg = folder / 'usage.reg' if feedback else None
        result = dict(compiled=True, feedback=feedback, packing=packing, optimize=level, model=model,
                      emphasis=emphasis, rom=rom, converged=(not feedback or history[-1]['sameImageAsPrevious'] and history[-1]['sameRegfileAsPrevious']),
                      passes=history, hardwareExecuted=False, cycleMeasurement=None)
    except Exception as error:
        result = dict(compiled=False, error=str(error), passes=history, hardwareExecuted=False)
    write(destination / 'result.json', result)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--keil', type=Path, required=True)
    parser.add_argument('--fixed-source', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--units', nargs='+', default=['main', 'callers', 'callees'])
    args = parser.parse_args()
    args.keil = args.keil.resolve()
    source = args.fixed_source.resolve()
    args.output.mkdir(parents=True, exist_ok=False)
    write(args.output / 'inputs.json', dict(keil=str(args.keil), source=str(source),
        tools={n: sha(args.keil / 'BIN' / n) for n in ['C251.exe', 'L251.exe', 'A251.exe', 'OH251.exe']},
        runtime={'C2SSH.LIB': sha(args.keil / 'LIB/C2SSH.LIB')},
        sources={p.name: sha(p) for p in source.iterdir() if p.suffix.lower() in ['.c', '.h', '.a51']},
        runnerSha256=sha(Path(__file__))))
    configs = [('baseline', {}), ('grc', {'feedback': True}),
               ('pack7', {'packing': True}), ('pack8', {'packing': True, 'level': 8}),
               ('pack9', {'packing': True, 'level': 9}),
               ('grc-pack9', {'feedback': True, 'packing': True, 'level': 9}),
               ('size7', {'emphasis': 'SIZE'})]
    results = {}
    for name, config in configs:
        result = build(args.keil, source, args.output / name, args.units, source / 'startup.a51', **config)
        results[name] = result
        write(args.output / 'results.json', results)
        print(name, result.get('error') or [p['codeBytes'] for p in result['passes']], flush=True)
    if any(not r.get('compiled') or not r.get('converged') for r in results.values()):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
