"""独立核对真实清单机器码与循环计数；不是实板计时或通用8051仿真器。"""

import argparse
import hashlib
import json
import re
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('validation', type=Path)
    args = parser.parse_args()
    results = []
    prefix = bytes.fromhex('CA0B7E01837E1182BE0400006818C0E975E9007E14')
    suffix = bytes.fromhex('9E14000178FA009E04000178EFD0E9DA0BAA')
    for file in sorted(args.validation.glob('*-*.json')):
        meta = json.loads(file.read_text(encoding='utf-8-sig'))
        if 'hz' not in meta:
            continue
        run = (
            Path(meta['evidenceDirectory'])
            if 'evidenceDirectory' in meta
            else Path(meta['artifacts'][0]).parent
        )
        listing = (run / 'ai8051u_delay.lst').read_text(encoding='utf8')
        rows = re.findall(r'^\s*([0-9A-F]{6})\s+((?:[0-9A-F]{2}\s+)+)\[\d+\]', listing, re.M)
        code = b''
        for address, raw in rows:
            assert int(address, 16) == len(code), (file, address)
            code += bytes.fromhex(raw)
        hz = meta['hz']
        # 与产品实现分开计算：每毫秒需要多少个4周期分组，最近整数再留一组给外圈。
        ticks = round(hz / 1000 / 4) - 1
        assert code == prefix + ticks.to_bytes(2, 'big') + suffix, (file, code.hex(), ticks)
        assert len(code) == 41
        # 原始相对分支目标：0ms到恢复DR0、内圈回SUB、外圈回装载计数。
        for pc, target in [(12, 38), (27, 23), (34, 19)]:
            assert pc + 2 + int.from_bytes(code[pc + 1 : pc + 2], 'big', signed=True) == target
        cases = []
        for ms in [0, 1, 255, 256, 500, 65535]:
            # WR0宽16位；ms==0先分支，绝不把0递减成65535。
            remaining = ms
            inner_trips = 0
            base_cycles = 0
            while remaining:
                inner_trips += ticks
                base_cycles += 1 + (4 * ticks - 2) + 1 + 1
                remaining -= 1
                base_cycles += 3 if remaining else 1
            assert inner_trips == ms * ticks
            if ms:
                assert base_cycles == ms * 4 * (ticks + 1) - 2
            cases.append(
                dict(
                    milliseconds=ms,
                    innerIterations=inner_trips,
                    loopCycles=base_cycles,
                    perTickMicroseconds=4 * (ticks + 1) * 1e6 / hz,
                )
            )
        results.append(
            dict(
                case=file.stem,
                hz=hz,
                loopCount=ticks,
                codeBytes=len(code),
                codeSha256=hashlib.sha256(code).hexdigest(),
                cases=cases,
            )
        )
    assert len(results) == 16, len(results)
    result = dict(
        passed=True,
        builds=results,
        hardwareMeasured=False,
        scope='Exact instruction bytes, relative branches and 16-bit loop boundaries; manual source-mode timing, excludes call/setup/restore and interrupt/IRC effects',
    )
    (args.validation / 'machine-code-audit.json').write_text(
        json.dumps(result, indent=2) + '\n', encoding='utf8'
    )
    print('PASS: 16 actual code images, 6 argument boundaries each; no hardware timing claim')


if __name__ == '__main__':
    main()
