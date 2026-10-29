"""离线验证全部寄存器声明、访问指令和拒绝边界；生成的寄存器探针禁止烧录。"""

from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sdcc', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    header = root / 'examples/packs/stc.stc251/include/AI8051U.h'
    data = json.loads((root / 'tools/data/ai8051u-registers.json').read_text(encoding='utf8'))
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    (output / 'AI8051U.h').write_bytes(header.read_bytes())
    (output / 'AI8051U_Delay.h').write_bytes((header.parent / 'AI8051U_Delay.h').read_bytes())
    lines = [
        '/* COMPILE ONLY: touches peripheral registers; NEVER flash this probe. */',
        '#include "AI8051U.h"',
        '#include "AI8051U.h"',
    ]
    for x in data['registers']:
        n = x['name']
        lines.append(f'uint8_t get_{n}(void) {{ return {n}; }}')
        if not x['readOnly']:
            lines.append(f'void set_{n}(void) {{ {n} = 0x5aU; }}')
    for x in data['bits']:
        n = x['name']
        lines.append(f'uint8_t getbit_{n}(void) {{ return {n}; }}')
        lines.append(f'void setbit_{n}(void) {{ {n} = 1; }}')
    # 在真实if/else中展开工具宏，防止多语句宏破坏控制流。
    lines += [
        'void helpers(uint8_t v) { if (v) EAXSFR(); else EAXRAM(); NOP(1); NOP(40); }',
        'uint8_t chip_id(uint8_t i) { return CHIPID[i] ^ CHIPIDX[i]; }',
    ]
    (output / 'all-registers.c').write_text('\n'.join(lines) + '\n', encoding='utf8')
    env = os.environ.copy()
    for key in [
        'SDCC_HOME',
        'SDCC_INCLUDE',
        'SDCC_LIB',
        'SDCC_BIN',
        'SDCCLIB',
        'SDCCDIR',
        'CPATH',
        'C_INCLUDE_PATH',
    ]:
        env.pop(key, None)
    results = []

    def run(label, flags, source, success=True):
        cmd = [str(args.sdcc.resolve()), *flags, '-c', source, '-o', label + '.rel']
        p = subprocess.run(cmd, cwd=output, env=env, capture_output=True, timeout=120)
        (output / (label + '.stdout.log')).write_bytes(p.stdout)
        (output / (label + '.stderr.log')).write_bytes(p.stderr)
        results.append(
            dict(label=label, command=cmd, exitCode=p.returncode, expectedSuccess=success)
        )
        (output / 'commands.json').write_text(json.dumps(results, indent=2) + '\n', encoding='utf8')
        if (p.returncode == 0) != success:
            raise AssertionError(label + ' unexpected compile result; see original logs')
        return (p.stdout + p.stderr).decode('utf8', errors='replace')

    for model in ['small', 'large']:
        for opt in ['speed', 'size']:
            label = f'all-{model}-{opt}'
            run(
                label,
                [
                    '-mmcs251',
                    '--model-' + model,
                    '--opt-code-' + opt,
                    '--std-sdcc11',
                    '--stack-auto',
                    '--mcs251-wide-ordinary',
                ],
                'all-registers.c',
            )
            asm = (output / (label + '.asm')).read_text(encoding='utf8')
            symbols = (output / (label + '.sym')).read_text(encoding='utf8')
            for x in data['registers'] + data['bits']:
                if x.get('kind') == 'xfr':
                    continue
                pattern = rf'^\s*_{re.escape(x["name"])}\s*=\s*{x["address"]:06X}\s+G\s*$'
                if not re.search(pattern, symbols, re.M):
                    raise AssertionError(label + ' SFR/bit symbol ' + x['name'])
            # 这些代表性地址来自独立的手册固定位置，不能把7E段截成16位。
            samples = {x['name']: x['address'] for x in data['registers'] if x['kind'] == 'xfr'}
            for name, addr in samples.items():
                block = asm.split('_get_' + name + ':', 1)[1].split('\teret', 1)[0]
                # 宽后端使用DR16间接字节读，传统回退使用DPTR/MOVX；两者都需保留7E段。
                native = (
                    f'mov\twr16,#0x{addr>>16:04x}' in block.lower()
                    and f'mov\twr18,#0x{addr&65535:04x}' in block.lower()
                    and re.search(r'\bmov\s+a,@dr16', block, re.I)
                )
                legacy = f'#0x{addr:06x}' in block.lower() and re.search(
                    r'\bmovx\s+a,@dptr', block, re.I
                )
                if not (native or legacy):
                    raise AssertionError(label + ' address/width sequence ' + name + '\n' + block)
                entry = next(x for x in data['registers'] if x['name'] == name)
                if not entry['readOnly']:
                    block = asm.split('_set_' + name + ':', 1)[1].split('\teret', 1)[0]
                    native = (
                        f'mov\twr16,#0x{addr>>16:04x}' in block.lower()
                        and f'mov\twr18,#0x{addr&65535:04x}' in block.lower()
                        and re.search(r'\bmov\s+@dr16,a', block, re.I)
                    )
                    legacy = f'#0x{addr:06x}' in block.lower() and re.search(
                        r'\bmovx\s+@dptr,a', block, re.I
                    )
                    if not (native or legacy):
                        raise AssertionError(label + ' store address/width ' + name + '\n' + block)
            nop_count = len(re.findall(r'^\s*nop\s*$', asm, re.M))
            if nop_count != 41:
                raise AssertionError(f'{label}: expected 41 NOP, got {nop_count}')
            print(
                'PASS ' + label + ' complete declarations, XFR address/width and NOP checks',
                flush=True,
            )
    for label, source in [('readonly-byte', 'CHIPID0 = 1;'), ('readonly-array', 'CHIPIDX[0] = 1;')]:
        (output / (label + '.c')).write_text(
            '#include "AI8051U.h"\nvoid f(void) { ' + source + ' }\n', encoding='utf8'
        )
        diagnostic = run(label, ['-mmcs251', '--std-sdcc11'], label + '.c', False)
        if 'const' not in diagnostic.lower():
            raise AssertionError('Wrong rejection: ' + diagnostic)
    (output / 'wrong-target.c').write_text('#include "AI8051U.h"\n', encoding='utf8')
    diagnostic = run('wrong-target', ['-mmcs51'], 'wrong-target.c', False)
    if 'requires MX251/SDCC mcs251' not in diagnostic:
        raise AssertionError('Wrong target diagnostic absent')
    summary = dict(
        passed=True,
        compilerSha256=hashlib.sha256(args.sdcc.read_bytes()).hexdigest(),
        headerSha256=hashlib.sha256(header.read_bytes()).hexdigest(),
        registerNames=len(data['registers']),
        bitNames=len(data['bits']),
        models=['small', 'large'],
        optimizations=['speed', 'size'],
        negativeCases=3,
        hardware=False,
    )
    (output / 'results.json').write_text(json.dumps(summary, indent=2) + '\n', encoding='utf8')
    print(json.dumps(summary))


if __name__ == '__main__':
    main()
