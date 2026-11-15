"""Small independent questions; the full calls experiment remains the size anchor."""
import argparse
from pathlib import Path
import shutil
import subprocess
from run import build, listing, write, sha


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--keil', type=Path, required=True)
    parser.add_argument('--startup', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--models', nargs='+', choices=['SMALL', 'LARGE'], default=['SMALL', 'LARGE'])
    parser.add_argument('--skip-call-widths', action='store_true')
    args = parser.parse_args()
    args.keil = args.keil.resolve()
    fixtures = Path(__file__).resolve().parent / 'fixtures'
    args.output.mkdir(parents=True, exist_ok=False)
    results = {}
    for model in args.models:
        for feedback in [False, True]:
            name = model.lower() + ('-grc' if feedback else '-baseline')
            result = build(args.keil, fixtures / 'registers', args.output / name,
                           ['main', 'callers', 'callees'], args.startup,
                           model=model, feedback=feedback)
            results[name] = result
            write(args.output / 'results.json', results)
            print(name, result.get('error') or [x['codeBytes'] for x in result['passes']], flush=True)
    for rom in ([] if args.skip_call_widths else ['HUGE', 'LARGE', 'MEDIUM', 'SMALL']):
        folder = args.output / ('call-widths-' + rom.lower())
        folder.mkdir()
        shutil.copyfile(fixtures / 'call-widths.c', folder / 'probe.c')
        command = [str(args.keil / 'BIN/C251.exe'), 'probe.c', 'SMALL', f'ROM({rom})', 'OPTIMIZE(7,SPEED)', 'CODE']
        result = subprocess.run(command, cwd=folder, capture_output=True, timeout=90)
        (folder / 'compile.stdout.log').write_bytes(result.stdout)
        (folder / 'compile.stderr.log').write_bytes(result.stderr)
        row = dict(command=command, exitCode=result.returncode, sourceSha256=sha(folder / 'probe.c'),
                   listing=listing(folder / 'probe.lst') if result.returncode == 0 else None)
        results['call-widths-' + rom.lower()] = row
        write(folder / 'result.json', row)
        write(args.output / 'results.json', results)
        print('call-widths', rom, result.returncode, flush=True)
    if any((x.get('exitCode') != 0 if k.startswith('call-widths') else
            not x.get('compiled') or not x.get('converged')) for k, x in results.items()):
        raise SystemExit(1)


if __name__ == '__main__':
    main()
