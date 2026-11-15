"""Reject mixed device profiles and invalidate owned stale output, without hardware access."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
from RunAcceptance import read_json, digest, write_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('profiles', 'acceptance', 'toolchain', 'cmake', 'ninja', 'pwsh', 'output'):
        parser.add_argument('--' + name, type=Path, required=True)
    args = parser.parse_args()
    validation = Path(__file__).resolve().parent
    adapter = validation.parent / 'build/qualified'
    matrix = read_json(validation / 'acceptance.json')
    prior = read_json(args.acceptance / 'results.json')
    if prior['status'] != 'passed' or prior['matrixSha256'] != digest(validation / 'acceptance.json'):
        raise ValueError('A passed current fixed-project acceptance is required')
    mapping = read_json(args.profiles)
    profiles = {key: (args.profiles.resolve().parent / value).resolve() for key, value in mapping.items()}
    for key, root in profiles.items():
        if digest(root / 'profile.lock.json') != matrix['profiles'][key]['profileLockSha256']:
            raise ValueError('Profile identity mismatch: ' + key)
    output = args.output.resolve()
    if any(output.is_relative_to(p.resolve()) for p in [validation, args.toolchain, args.acceptance, *profiles.values()]):
        raise ValueError('Output must be separate from all inputs')
    output.mkdir(parents=True, exist_ok=False)
    results = []
    summary = dict(status='running', hardwareAccess=False, matrixSha256=digest(validation / 'acceptance.json'),
                   runnerSha256=digest(Path(__file__)), cases=results,
                   staleOutput='Synthetic owned cache: verified complete HEX and receipt, plus a BIN sentinel; never downloaded.')
    env = os.environ.copy()
    for key in ('CFLAGS', 'CXXFLAGS', 'LDFLAGS', 'CPATH', 'C_INCLUDE_PATH', 'SDCC_HOME', 'SDCC_INCLUDE', 'SDCC_LIB'):
        env.pop(key, None)
    env.update(POWERSHELL_TELEMETRY_OPTOUT='1', POWERSHELL_UPDATECHECK='Off')
    commands = []

    def run(directory, label, command):
        command = list(map(str, command))
        with (directory / (label + '.stdout.log')).open('wb') as out, (directory / (label + '.stderr.log')).open('wb') as err:
            p = subprocess.run(command, cwd=directory, env=env, stdout=out, stderr=err, timeout=180)
        commands.append(dict(case=directory.name, label=label, command=command, exitCode=p.returncode))
        write_json(output / 'commands.json', commands)
        return p.returncode

    faults = [
        ('stc-ai-startup', 'STC32G12K128', 'startup-other', 'Startup does not match target contract'),
        ('ai-stc-startup', 'AI8051U-34K64-32', 'startup-other', 'Startup does not match target contract'),
        ('missing-startup-lock', 'STC32G12K128', 'startup-missing', 'Startup does not match target contract'),
        ('duplicate-startup-lock', 'STC32G12K128', 'startup-duplicate', 'Startup does not match target contract'),
        ('missing-float-library-lock', 'STC32G12K128', 'library-missing', 'Runtime library must be pinned exactly once'),
        ('duplicate-float-library-lock', 'STC32G12K128', 'library-duplicate', 'Runtime library must be pinned exactly once'),
        ('missing-ai-mdu-library-lock', 'AI8051U-34K64-32', 'mdu-library-missing', 'Runtime library must be pinned exactly once'),
        ('duplicate-ai-mdu-library-lock', 'AI8051U-34K64-32', 'mdu-library-duplicate', 'Runtime library must be pinned exactly once'),
        ('wrong-contract-id', 'STC32G12K128', 'contract-id', 'Target contract lock mismatch'),
        ('wrong-contract-hash', 'STC32G12K128', 'contract-hash', 'Target contract lock mismatch'),
        ('stale-ai-handoff', 'AI8051U-34K64-32', 'handoff', 'Unknown profile lock property: interruptHandoff'),
        ('stale-ai-vectors', 'AI8051U-34K64-32', 'vectors', 'Unknown profile lock property: interruptVectors'),
        ('missing-overlay', 'STC32G12K128', 'overlay', 'Target contract requires a pinned compiler overlay'),
    ]
    try:
        for name, target, fault, diagnostic in faults:
            directory = output / name
            directory.mkdir()
            profile = directory / 'profile'
            shutil.copytree(profiles[target], profile)
            lock = read_json(profile / 'profile.lock.json')
            if fault == 'startup-other':
                other = next(root for key, root in profiles.items() if key != target)
                shutil.copy2(other / 'startup.asm', profile / 'startup.asm')
                # 攻击的是器件混用，而不是普通哈希损坏：更新自述哈希后也必须被目标契约拒绝。
                next(e for e in lock['profile'] if e['path'] == 'startup.asm')['sha256'] = digest(profile / 'startup.asm')
            elif fault in ('startup-missing', 'startup-duplicate', 'library-missing', 'library-duplicate',
                           'mdu-library-missing', 'mdu-library-duplicate'):
                path = ('startup.asm' if fault.startswith('startup') else
                        'runtime/libai8051umdu32.lib' if fault.startswith('mdu-') else 'runtime/libnativefloat.lib')
                entry = next(e for e in lock['profile'] if e['path'] == path)
                if fault.endswith('missing'):
                    lock['profile'].remove(entry)
                else:
                    lock['profile'].append(entry.copy())
            elif fault == 'contract-id':
                lock['targetContract']['id'] = 'AI8051U-34K64-32'
            elif fault == 'contract-hash':
                lock['targetContract']['registrySha256'] = '0' * 64
            elif fault == 'handoff':
                lock['interruptHandoff'] = {'target': 'STC32G12K128'}
            elif fault == 'vectors':
                lock['interruptVectors'] = {'count': 62, 'unused': 'disable-ea-and-halt'}
            elif fault == 'overlay':
                lock.pop('compilerOverlay')
            write_json(profile / 'profile.lock.json', lock)
            seed_id = 'ai-callbacks-small' if target.startswith('AI') else 'math-software'
            if fault.startswith('mdu-'):
                # 使用实际选择MDU库的完整工程，验证文件存在但未唯一锁定时也会拒绝。
                seed_id = 'ai-fixed-integer-small'
            case = next(c for c in matrix['cases'] if c['id'] == seed_id)
            seed = args.acceptance / seed_id / 'project'
            project = directory / 'project'
            shutil.copytree(seed, project, ignore=shutil.ignore_patterns('.build'))
            build = project / '.build/cmake'
            code = run(directory, 'configure', [args.cmake.resolve(), '-S', project, '-B', build, '-G', 'Ninja',
                '-DCMAKE_MAKE_PROGRAM=' + args.ninja.resolve().as_posix(),
                '-DMX251_PROJECT_MODULE=' + (adapter / 'Project.cmake').as_posix(),
                '-DMX251_BUILD_SCRIPT=' + (adapter / 'Build.ps1').as_posix(),
                '-DMX251_PWSH=' + args.pwsh.resolve().as_posix(),
                '-DMX251_PROFILE=' + profile.as_posix(), '-DMX251_TOOLCHAIN=' + args.toolchain.resolve().as_posix()])
            if code:
                raise RuntimeError(name + ': configure failed')
            latest = project / '.build/mx251/latest'
            latest.mkdir(parents=True)
            seed_latest = seed / '.build/mx251/latest'
            if digest(seed_latest / 'firmware.hex') != case['firmwareSha256']:
                raise ValueError('Stale seed does not match complete baseline')
            for file in ('firmware.hex', 'receipt.json'):
                shutil.copy2(seed_latest / file, latest / file)
            (latest / 'firmware.bin').write_bytes(b'STALE-BIN-TEST-SENTINEL')
            write_json(latest / 'owner.json', dict(schema=1, project=str(project / 'mx251.json')))
            code = run(directory, 'build', [args.cmake.resolve(), '--build', build, '--target', 'firmware'])
            logs = '\n'.join(p.read_text(encoding='utf-8', errors='replace') for p in directory.glob('build.*.log'))
            if code == 0 or diagnostic not in logs:
                raise RuntimeError(name + ': expected diagnostic missing')
            if any((latest / f).exists() for f in ('firmware.hex', 'firmware.bin', 'receipt.json', 'commands.json')):
                raise RuntimeError(name + ': stale success output or compiler execution found')
            if read_json(latest / 'build-state.json')['status'] != 'failed':
                raise RuntimeError(name + ': incorrect output state')
            results.append(dict(id=name, status='passed', target=target, diagnostic=diagnostic,
                                rejectedBeforeCompiler=True, staleSuccessOutputRemoved=True,
                                profileLockSha256=digest(profile / 'profile.lock.json')))
            write_json(output / 'results.json', summary)
            print('PASS ' + name, flush=True)
        summary['status'] = 'passed'
    except Exception as error:
        summary.update(status='failed', error=str(error))
        raise
    finally:
        write_json(output / 'results.json', summary)


if __name__ == '__main__':
    main()
