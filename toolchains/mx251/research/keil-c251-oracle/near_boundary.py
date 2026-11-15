"""Verify that a near-call declaration is a checked placement contract."""
import argparse
from pathlib import Path
import subprocess
from run import write


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--keil', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    keil = args.keil.resolve()
    caller = '#pragma USERCLASS(UCODE=BANK0)\nextern void near remote(void);\nvoid main(void) { remote(); for (;;) {} }\n'
    callee = '#pragma USERCLASS(UCODE=BANK1)\nvolatile unsigned char observed;\nvoid near remote(void) { ++observed; }\n'
    results = {}
    for name, segment in [('same-segment', '0xFF0200-0xFF02FF'), ('cross-segment', '0xFE0200-0xFE02FF')]:
        folder = args.output / name
        folder.mkdir()
        commands = []
        for unit, source in [('caller', caller), ('callee', callee)]:
            (folder / (unit + '.c')).write_text(source, encoding='utf8')
            command = [str(keil / 'BIN/C251.exe'), unit + '.c', 'SMALL', 'ROM(HUGE)', 'CODE']
            process = subprocess.run(command, cwd=folder, capture_output=True, timeout=90)
            (folder / (unit + '.log')).write_bytes(process.stdout + process.stderr)
            commands.append(dict(command=command, exitCode=process.returncode))
            assert process.returncode == 0
        # A main module references the startup symbol even in a link-only probe.
        (folder / 'startup.a51').write_text('$NOMOD51\n$MODSRC\nNAME BOOT\nEXTRN ECODE (MAIN?)\nPUBLIC ?C?STARTUP?\nCSEG AT 100H\n?C?STARTUP?:\nEJMP MAIN?\nEND\n', encoding='ascii')
        command = [str(keil / 'BIN/A251.exe'), 'startup.a51']
        process = subprocess.run(command, cwd=folder, capture_output=True, timeout=90)
        (folder / 'startup.log').write_bytes(process.stdout + process.stderr)
        commands.append(dict(command=command, exitCode=process.returncode))
        assert process.returncode == 0
        command = [str(keil / 'BIN/L251.exe'), 'caller.obj,callee.obj,startup.obj', 'TO', 'probe', 'NODEFAULTLIBRARY',
                   'CLASSES(ECODE_BANK0(0xFF0000-0xFF00FF),ECODE_BANK1(' + segment + '))']
        process = subprocess.run(command, cwd=folder, capture_output=True, timeout=90)
        (folder / 'link.log').write_bytes(process.stdout + process.stderr)
        commands.append(dict(command=command, exitCode=process.returncode))
        write(folder / 'commands.json', commands)
        results[name] = dict(exitCode=process.returncode, output=(process.stdout + process.stderr).decode(errors='replace'))
        write(args.output / 'results.json', results)
        print(name, process.returncode)
    assert results['same-segment']['exitCode'] == 0
    assert results['cross-segment']['exitCode'] != 0 and 'FIXUP' in results['cross-segment']['output']


if __name__ == '__main__':
    main()
