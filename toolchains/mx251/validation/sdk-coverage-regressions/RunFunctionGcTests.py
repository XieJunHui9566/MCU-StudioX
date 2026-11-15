"""Run portable GC safety properties against an explicitly selected production engine."""
import argparse
import hashlib
import json
import shutil
import subprocess
import sys
from pathlib import Path


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--engine', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--powershell', default='pwsh')
    args = parser.parse_args()
    engine = args.engine.resolve(strict=True)
    output = args.output.resolve()
    if output.exists():
        parser.error('Output exists; preserve it and choose a new evidence directory.')
    shell = shutil.which(args.powershell)
    if shell is None:
        parser.error('PowerShell 7 executable was not found.')
    required = {'Test-FunctionGc.ps1', 'Test-FunctionGcBoundaries.ps1',
                'Test-FunctionGcBitOperands.ps1', 'Test-FunctionGcImports.ps1',
                'Test-FunctionGcSymbolCase.ps1', 'Test-FunctionGcImplicitRoots.ps1'}
    scripts = [Path(__file__).parent / name for name in sorted(required)]
    startup = Path(__file__).parent / 'startup-link-roots/replay.py'
    if not all(script.is_file() for script in scripts) or not startup.is_file():
        parser.error('Safety fixture scripts are missing.')
    output.mkdir(parents=True)
    result = {'scope': 'current-engine symbol/syntax properties and frozen startup counterexample replay; no hardware or performance qualification',
              'engine': str(engine), 'engineSha256': digest(engine), 'tests': [], 'passed': False}
    for script in scripts:
        target = output / script.stem
        argv = [shell, '-NoProfile', '-File', str(script.resolve()),
                '-Engine', str(engine), '-OutputDirectory', str(target)]
        try:
            run = subprocess.run(argv, capture_output=True, timeout=60)
            code, stdout, stderr = run.returncode, run.stdout, run.stderr
        except subprocess.TimeoutExpired as error:
            code, stdout, stderr = 124, error.stdout or b'', (error.stderr or b'') + b'\nTIMEOUT\n'
        (output / (script.stem + '.stdout.log')).write_bytes(stdout)
        (output / (script.stem + '.stderr.log')).write_bytes(stderr)
        evidence = target / 'results.json'
        result['tests'].append({'script': script.name, 'sha256': digest(script), 'argv': argv,
                                'exitCode': code, 'results': str(evidence),
                                'resultsSha256': digest(evidence) if evidence.is_file() else None})
        (output / 'results.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    # 同一入口强制重放启动反例，防止只看最终机器字节就误认初始化契约正确。
    # 此项检查冻结证据；当前候选仍必须另外重建、核对完整工程的启动闭包。
    startup_output = output / 'startup-link-roots'
    argv = [sys.executable, '-B', str(startup.resolve()), '--output', str(startup_output)]
    try:
        run = subprocess.run(argv, capture_output=True, timeout=60)
        code, stdout, stderr = run.returncode, run.stdout, run.stderr
    except subprocess.TimeoutExpired as error:
        code, stdout, stderr = 124, error.stdout or b'', (error.stderr or b'') + b'\nTIMEOUT\n'
    (output / 'startup-link-roots.stdout.log').write_bytes(stdout)
    (output / 'startup-link-roots.stderr.log').write_bytes(stderr)
    startup_result = startup_output / 'results.json'
    replay = json.loads(startup_result.read_text(encoding='utf-8')) if startup_result.is_file() else {}
    result['startupCounterexampleReplay'] = {
        'script': str(startup), 'sha256': digest(startup), 'argv': argv, 'exitCode': code,
        'status': replay.get('status', 'MISSING'), 'results': str(startup_result),
        'resultsSha256': digest(startup_result) if startup_result.is_file() else None,
        'currentCandidateBuildQualification': False,
    }
    result['engineUnchanged'] = digest(engine) == result['engineSha256']
    result['passed'] = (result['engineUnchanged']
                        and all(t['exitCode'] == 0 and t['resultsSha256'] for t in result['tests'])
                        and code == 0 and replay.get('status') == 'PASS')
    (output / 'results.json').write_text(json.dumps(result, indent=2) + '\n', encoding='utf-8')
    print(json.dumps({'passed': result['passed'], 'suiteCount': len(result['tests']), 'output': str(output)}))
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
