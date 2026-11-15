"""Build the unchanged 64-member small/stack-auto native float recipe.

Explicit local inputs; CMake owns the member graph and Ninja executes it.
This builds a candidate archive, not target or hardware qualification.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


# 成员顺序是归档契约的一部分；不从旧二进制归档猜测源码或引入旧对象。
MEMBERS = """_atof _schar2fs _sint2fs _slong2fs _slonglong2fs _uchar2fs
_uint2fs _ulong2fs _ulonglong2fs _fs2schar _fs2sint _fs2slong _fs2slonglong
_fs2uchar _fs2uint _fs2ulong _fs2ulonglong _fsadd _fsdiv _fsmul _fssub _fseq
_fslt _fsneq fabsf frexpf ldexpf expf powf sincosf sinf cosf logf log10f sqrtf
tancotf tanf cotf asincosf asinf acosf atanf atan2f sincoshf sinhf coshf tanhf
floorf ceilf modff errno isinf isnan _fscmp _fsget1arg _fsget2args _fsnormalize
_fsreturnval _fsrshift _fsswapargs _logexpf _fsle sqrt_domain fsmul_u16""".split()
CONVERSIONS = {'_fseq': 'eq', '_fsneq': 'ne', '_fslt': 'lt', '_fsle': 'le',
               '_ulong2fs': 'u2f', '_slong2fs': 's2f', '_fs2ulong': 'f2u', '_fs2slong': 'f2s'}
CORES = {'_fsadd': 'fsadd_bits', '_fssub': 'fssub_bits', '_fsdiv': 'fsdiv_bits',
         '_fsmul': 'fsmul_bits', 'sqrtf': 'sqrt_bits'}
BASE_FLAGS = ['-mmcs251', '--model-small', '--stack-auto', '--opt-code-size',
              '--nostdinc', '--std-c23']


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def recipe(name):
    if name in CONVERSIONS:
        stem = 'bits_' + CONVERSIONS[name]
        return stem, 'device/lib/mcs251/native-float/' + stem + '.c', ['--mcs251-wide-backend']
    if name in CORES:
        stem = CORES[name]
        return stem, 'device/lib/mcs251/native-float/' + stem + '.c', ['--mcs251-wide-backend']
    if name in ('sqrt_domain', 'fsmul_u16'):
        return name, 'device/lib/mcs251/native-float/' + name + '.c', ['--mcs251-native-abi']
    flags = ['--mcs251-native-runtime', '--mcs251-native-int-runtime']
    if name in ('sincosf', 'tancotf'):
        flags += ['--mcs251-wide-backend']
    return name, 'device/lib/' + name + '.c', flags


def environment(config):
    # 仅子进程使用显式工具目录，避免用户环境替换预处理器、头文件或库。
    env = {k.upper(): v for k, v in os.environ.items()}
    for key in ('SDCC_HOME', 'SDCC_INCLUDE', 'SDCC_LIB', 'SDCC_BIN', 'SDCCLIB',
                'SDCCDIR', 'CPATH', 'C_INCLUDE_PATH', 'CFLAGS', 'CXXFLAGS', 'LDFLAGS'):
        env.pop(key, None)
    env['SDCC_HOME'] = config['toolchain']
    system = Path(env['SYSTEMROOT'])
    env['PATH'] = ';'.join(map(str, [Path(config['toolchain']) / 'bin', system / 'System32', system]))
    env['SOURCE_DATE_EPOCH'] = '0'
    env['PYTHONDONTWRITEBYTECODE'] = '1'
    return env


def run(config, directory, label, args):
    args = list(map(str, args))
    record = {'arguments': args, 'cwd': str(directory)}
    write(directory / (label + '.command.json'), record)
    with (directory / (label + '.stdout.log')).open('wb') as out, (directory / (label + '.stderr.log')).open('wb') as err:
        process = subprocess.run(args, cwd=directory, env=environment(config), stdout=out, stderr=err)
    record['exitCode'] = process.returncode
    write(directory / (label + '.command.json'), record)
    if process.returncode:
        raise RuntimeError(label + ' failed; original logs: ' + str(directory))


def compile_member(config, name):
    if name not in MEMBERS:
        raise ValueError('Unknown native float member: ' + name)
    source = Path(config['source'])
    directory = Path(config['output']) / 'objects'
    stem, relative, extra = recipe(name)
    flags = BASE_FLAGS + ['-I' + str(source / 'device/include'), '-I' + str(source / 'device/include/mcs51')] + extra
    run(config, directory, name + '-compile', [config['compiler'], *flags, '-c', source / relative, '-o', stem + '.rel'])
    accepted = []
    if '--mcs251-wide-backend' in extra:
        assembly = (directory / (stem + '.asm')).read_text(encoding='utf8')
        accepted = re.findall(r'wide-mir accepted:.*', assembly)
        if not accepted or 'wide-mir fallback:' in assembly:
            raise ValueError('Native float wide admission changed: ' + name)
        if name == 'sincosf':
            if len(accepted) != 1 or 'mcs251_sincos_poly' not in accepted[0]:
                raise ValueError('sincosf wide helper contract changed')
        elif name == 'tancotf':
            helpers = ['mcs251_product_bit', 'mcs251_multiply_two_over_pi', 'mcs251_reduce_fraction', 'mcs251_tancot_poly']
            if len(accepted) != 4 or any(helper not in row for helper, row in zip(helpers, accepted)):
                raise ValueError('tancotf wide helper contract changed')
        elif name == '_fsmul':
            assembly = re.sub(r'^\s*\.module[^\n]*\n', '', assembly.replace('___m251r4___fsmul_bits', '___m251r4___fsmul_fallback'), flags=re.M)
            assembly = '.module _fsmul\n.area CSEG (CODE)\n' + (source / 'device/lib/mcs251/native-float/fsmul_fast.inc').read_text(encoding='utf8') + '\n' + assembly
        elif name in ('_fsadd', '_fssub'):
            for old, new in [('__fsadd_bits', '__fsadd'), ('__fssub_bits', '__fssub')]:
                assembly = assembly.replace('___m251r4_' + old, '___m251r4_' + new)
        elif name == '_fsdiv':
            assembly = assembly.replace('___m251r4___fsdiv_bits', '___m251r4___fsdiv')
        elif name == 'sqrtf':
            assembly = assembly.replace('___m251r4___sqrt_bits', '___m251r4_sqrtf')
        else:
            # 保留原 ABI2 入口到 ABI4 位模式核心的字节布局和返回搬运。
            bridge = ['.area CSEG (CODE)', '.globl __' + name, '__' + name + ':',
                      'mov r4,a', 'mov r5,b', 'mov r6,dph', 'mov r7,dpl']
            comparison = CONVERSIONS[name] in ('eq', 'ne', 'lt', 'le')
            if comparison:
                bridge += ['mov wr12,@spx-6', 'mov wr14,@spx-4']
            bridge += ['ecall ___m251r4_' + stem]
            bridge += ['mov dpl,r7'] if comparison else ['mov a,r4', 'mov b,r5', 'mov dph,r6', 'mov dpl,r7']
            assembly += '\n' + '\n'.join(bridge + ['eret']) + '\n'
        (directory / (name + '.asm')).write_text(assembly, encoding='utf8')
        run(config, directory, name + '-assemble', [Path(config['toolchain']) / 'bin/sdas251.exe', '-plosgff', name + '.rel', name + '.asm'])
    member = directory / (name + '.rel')
    size = re.search(r'^A CSEG size ([0-9A-F]+) ', member.read_text(encoding='utf8'), re.M)
    if not size:
        raise ValueError('Missing CSEG: ' + name)
    write(directory / (name + '.complete.json'), {'member': name + '.rel', 'source': relative,
          'sha256': digest(member), 'codeBytes': int(size[1], 16), 'wideAccepted': accepted})


def create_archive(config):
    objects = Path(config['output']) / 'objects'
    pending = objects / 'libnativefloat.pending.lib'
    run(config, objects, 'archive', [Path(config['toolchain']) / 'bin/sdar.exe', '-rcsD', pending.name, *[n + '.rel' for n in MEMBERS]])


def archive_members(path):
    data = path.read_bytes()
    if data[:8] != b'!<arch>\n':
        raise ValueError('Invalid archive signature')
    offset, names, result = 8, b'', {}
    while offset < len(data):
        header = data[offset:offset + 60]
        if len(header) != 60 or header[58:] != b'`\n':
            raise ValueError('Invalid archive member header')
        count = int(header[48:58]); name = header[:16].decode('ascii').strip()
        payload = data[offset + 60:offset + 60 + count]
        if len(payload) != count:
            raise ValueError('Truncated archive member')
        offset += 60 + count + count % 2
        if name == '//':
            names = payload; continue
        if name == '/':
            continue
        if name.startswith('/'):
            start = int(name[1:]); end = names.find(b'/\n', start)
            if end < start:
                raise ValueError('Invalid archive long name')
            name = names[start:end].decode('ascii')
        else:
            name = name.rstrip('/')
        if name in result:
            raise ValueError('Duplicate archive member')
        result[name] = payload
    return result


def input_inventory(config):
    source = Path(config['source']); paths = {Path(__file__).resolve(), Path(sys.executable).resolve()}
    paths.update(source / recipe(name)[1] for name in MEMBERS)
    paths.update(p for p in (source / 'device/include').rglob('*') if p.is_file())
    paths.update(p for p in (source / 'device/lib').rglob('*') if p.is_file() and p.suffix in ('.h', '.inc'))
    # 汇编器、预处理器、归档器及其 DLL 都是实际构建输入。
    paths.update(p for p in (Path(config['toolchain']) / 'bin').iterdir() if p.is_file())
    for name in ('compiler', 'cmake', 'ninja'):
        tool = Path(config[name]); paths.add(tool); paths.update(tool.parent.glob('*.dll'))
    return [{'path': str(p), 'sha256': digest(p)} for p in sorted(paths)]


def cmake_quote(value):
    # CMake bracket arguments prevent path interpolation, including spaces and '$'.
    value = str(value).replace('\\', '/')
    if ']==]' in value or ';' in value or '\n' in value or '\r' in value:
        raise ValueError('Unsupported CMake argument character')
    return '[==[' + value + ']==]'


def build(args):
    config = {name: str(getattr(args, name).resolve(strict=True)) for name in ('source', 'compiler', 'toolchain', 'cmake', 'ninja')}
    output = args.output.resolve()
    roots = [Path(config['source']), Path(config['toolchain']), Path(__file__).resolve().parent]
    if output.exists() or any(output == root or output.is_relative_to(root) for root in roots):
        raise ValueError('Output must be new and outside source, toolchain and builder inputs')
    for path in [Path(config[n]) for n in ('compiler', 'cmake', 'ninja')] + [Path(config['toolchain']) / 'bin' / n for n in ('sdcpp.exe', 'sdas251.exe', 'sdar.exe')]:
        if not path.is_file():
            raise FileNotFoundError(path)
    if len(MEMBERS) != 64 or len(set(MEMBERS)) != 64:
        raise ValueError('Invalid native float member recipe')
    config['output'] = str(output)
    inventory = input_inventory(config)
    output.mkdir(parents=True, exist_ok=False); (output / 'objects').mkdir()
    try:
        write(output / 'input-lock.json', inventory); write(output / 'build-config.json', config)
        cmake = ['cmake_minimum_required(VERSION 3.25)', 'project(MX251NativeFloat NONE)']
        dependencies = ' '.join(cmake_quote(row['path']) for row in inventory)
        script = Path(__file__).resolve(); python = Path(sys.executable).resolve()
        for name in MEMBERS:
            completed = output / 'objects' / (name + '.complete.json')
            command = [python, '-B', str(script), '--worker', output / 'build-config.json', '--member', name]
            cmake += ['add_custom_command(OUTPUT ' + cmake_quote(completed) + '\n COMMAND ' + ' '.join(map(cmake_quote, command)) + '\n DEPENDS ' + dependencies + '\n VERBATIM)']
        stamps = [output / 'objects' / (n + '.complete.json') for n in MEMBERS]
        pending = output / 'objects/libnativefloat.pending.lib'
        command = [python, '-B', str(script), '--worker', output / 'build-config.json', '--archive']
        cmake += ['add_custom_command(OUTPUT ' + cmake_quote(pending) + '\n COMMAND ' + ' '.join(map(cmake_quote, command)) + '\n DEPENDS ' + ' '.join(map(cmake_quote, stamps)) + '\n VERBATIM)',
                  'add_custom_target(native_float ALL DEPENDS ' + cmake_quote(pending) + ')']
        (output / 'CMakeLists.txt').write_text('\n'.join(cmake) + '\n', encoding='utf8')
        run(config, output, 'configure', [config['cmake'], '-S', output, '-B', output / 'build', '-G', 'Ninja', '-DCMAKE_MAKE_PROGRAM=' + config['ninja']])
        run(config, output, 'build', [config['cmake'], '--build', output / 'build', '--parallel', '4'])
        objects = output / 'objects'; records = []
        for name in MEMBERS:
            row = json.loads((objects / (name + '.complete.json')).read_text(encoding='utf8'))
            if row['sha256'] != digest(objects / (name + '.rel')):
                raise ValueError('Member changed after compilation: ' + name)
            records.append(row)
        contents = archive_members(pending)
        if list(contents) != [n + '.rel' for n in MEMBERS] or any(contents[n + '.rel'] != (objects / (n + '.rel')).read_bytes() for n in MEMBERS):
            raise ValueError('Archive member set, order or bytes differ')
        if input_inventory(config) != inventory:
            raise ValueError('Source or tools changed during build')
        receipt = {'schema': 1, 'status': 'complete', 'runtime': 'native-float', 'model': 'small-stack-auto',
                   'buildChain': ['CMake', 'Ninja', 'MX251'], 'inputLockSha256': digest(output / 'input-lock.json'),
                   'compilerSha256': digest(Path(config['compiler'])), 'archiveSha256': digest(pending),
                   'members': records, 'hardwareExecuted': False, 'qualificationGranted': False}
        write(output / 'receipt.pending.json', receipt)
        pending.replace(output / 'libnativefloat.lib')
        (output / 'receipt.pending.json').replace(output / 'receipt.json')
        print(json.dumps({'archive': str(output / 'libnativefloat.lib'), 'sha256': receipt['archiveSha256'], 'members': 64}))
    except BaseException as error:
        # 目录由本次排他创建；失败不保留可被误用的最终库或成功凭据。
        for name in ('libnativefloat.lib', 'receipt.json', 'receipt.pending.json'):
            (output / name).unlink(missing_ok=True)
        write(output / 'failure.json', {'status': 'failed', 'error': str(error), 'type': type(error).__name__})
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ('source', 'compiler', 'toolchain', 'cmake', 'ninja', 'output'):
        parser.add_argument('--' + name, type=Path)
    parser.add_argument('--worker', type=Path, help=argparse.SUPPRESS)
    parser.add_argument('--member', help=argparse.SUPPRESS)
    parser.add_argument('--archive', action='store_true', help=argparse.SUPPRESS)
    args = parser.parse_args()
    if args.worker:
        if bool(args.member) == args.archive:
            parser.error('Internal worker requires exactly one member or archive')
        config = json.loads(args.worker.read_text(encoding='utf8'))
        create_archive(config) if args.archive else compile_member(config, args.member)
    else:
        if any(getattr(args, name) is None for name in ('source', 'compiler', 'toolchain', 'cmake', 'ninja', 'output')):
            parser.error('--source --compiler --toolchain --cmake --ninja --output are required')
        build(args)


if __name__ == '__main__':
    main()
