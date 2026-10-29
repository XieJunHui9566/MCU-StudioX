"""将已封存 R89 转为 StudioX 格式 1 组件；不下载、不覆盖已有版本。"""

from pathlib import Path
import argparse, hashlib, json, shutil, zipfile


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def archive(root, output):
    with zipfile.ZipFile(output, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for file in sorted(root.rglob('*')):
            if file.is_file():
                info = zipfile.ZipInfo(file.relative_to(root).as_posix(), (2026, 10, 26, 0, 0, 0))
                info.compress_type = zipfile.ZIP_DEFLATED
                z.writestr(info, file.read_bytes())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--qualification', type=Path, required=True)
    parser.add_argument('--powershell', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    q = args.qualification.resolve()
    o = q.parent
    t = o / 'round87/toolchain'
    out = args.output.resolve()
    assert q.name == 'round89' and not out.exists(), 'Require sealed R89 and a new output directory'
    assert (
        digest(q / 'profile/profile.lock.json')
        == '5bd1d39824c9cb85fd26a1b63e25081f9181f1ecb10cdb4d96c4acc0d72dd350'
    )
    assert (
        digest(t / 'bin/sdcc.exe')
        == '6b257de531614340c295ab1c12e080cec6e170f24942afa949cbacadbc172733'
    )
    for entry in read(q / 'profile/profile.lock.json')['toolchain']:
        assert digest(t / entry['path']) == entry['sha256'], entry['path']
    out.mkdir(parents=True)
    component = out / 'staging/toolsets/studiox.mx251/0.1.89'
    component.mkdir(parents=True)
    for entry in read(q / 'profile/profile.lock.json')['toolchain']:
        target = component / 'sdcc' / entry['path']
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(t / entry['path'], target)
    # sdcpp是驱动程序，还需要相对安装目录中的cc1；资格锁的163项并非部署依赖全集。
    shutil.copytree(t / 'libexec', component / 'sdcc/libexec')
    shutil.copytree(t / 'licenses', component / 'sdcc/licenses')
    shutil.copyfile(t / 'COPYING', component / 'sdcc/COPYING')
    shutil.copytree(q / 'profile', component / 'profile')
    # PowerShell 为组件内的自包含运行环境，用户无需修改 PATH 或另装运行时。
    assert (args.powershell / 'pwsh.exe').is_file() and (args.powershell / 'LICENSE.txt').is_file()
    shutil.copytree(args.powershell, component / 'powershell')
    provenance = read(component / 'profile/runtime-provenance.json')
    for item in provenance:
        item['source'] = 'sources/native-runtime/' + (
            'float' if 'float' in item['path'] else 'integer'
        )
    write(component / 'profile/runtime-provenance.json', provenance)
    docs = component / 'docs'
    docs.mkdir()
    usage = '''MX251 — MCU StudioX 251 Compiler — R89 — MCU StudioX 本地试用组件 0.1.89

MX251 是基于 SDCC 4.6.0 分支持续修改的编译器工具链，保留上游版权、许可证及对应源码。
内部 sdcc.exe、目录名及历史资格记录沿用原标识，以保持冻结核心的哈希和调用兼容。
本包由已封存R89构建入口和R87编译器/运行库产生，编译器机器码未修改。
需要本次支持MX251的MCU StudioX构建；旧版IDE会拒绝此用途，不应改名冒充8位SDCC。
1. 在“开发环境组件”导入 studiox.mx251-0.1.89-win-x64.mcutoolchain。
2. 在器件包管理导入 stc.ai8051u-mx251-0.1.89.mcupack。
3. 新建 STC → AI8051U-34K64 → 普通C small 或 large 工程，按F7。
4. HEX在 .build/mx251/本次运行目录；IDE输出显示路径，原始工具日志和MAP一起保留。

参数和源码在工程根 mx251.json 显式登记，不使用CMake或GCC选项。
新增C/H需要加入files，C翻译单元加入units并恰好一次进入link或已链接archive。
头文件使用相对路径，例如src/main.c包含../include/board.h；R89入口没有任意-I参数。
普通C可选small/large、speed/size、default/sdcc11，ordinary-wide后端与standard运行库。
显式ABI4须声明和定义一致使用__reentrant __sdcccall(4)，不能将所有函数自动转ABI。
运行库standard/native-int/native-math/native-math-wide的限定组合见profile/README.txt。
此README保留原资格时间点的暂停/未发行描述，本次用户已明确授权制作组件，资格范围不变。

建议先用普通C模板接入真实工程。examples/fixed保留30个已验证配置和源码，
可在IDE新建工程后将对应源码和配置相对路径复制调整至mx251.json；不可混用ABI和库。
命令行也可用组件内powershell/pwsh.exe调用profile/Build-251.ps1，显式传
-Project、-ToolchainRoot（组件sdcc子目录）、-OutputDirectory（尚不存在的输出目录）。

仅AI8051U-34K64的32位模式已资格；8位模式/其它STC32芯片不属于本包承诺。
IDE下载/调试未开放；手工STC-ISP下载选择准确芯片与32-Bit，不能用8位STC入口。
CODE=0xff0000起64KiB，物理XDATA=32KiB，当前分配28KiB，EDATA栈0x400–0x7ff。
没有动态栈溢出保护。符号DR立即数汇编边界尚在，未审计手写汇编排除。
ABI4 code指针返回error365，改用普通ABI。公共bit ABI、任意ISR和完整语言合规未全面认证。
数学自测267/268仅已知硬件NaN分类差异；软件回归无失败不等于完整IEEE754认证。
clangd只做通用C编辑索引，不验证目标位宽、ABI或每个unit的完整宏环境，实际MX251编译诊断为准。

本包不改系统PATH、不安装Python、不替换stc.sdcc。组件按工程精确版本及内容哈希锁定。
编译失败/取消不发布当前工程成功凭据，原始日志保留。历史成功目录不作为本次成功产物。
来源与许可证在profile/licenses、powershell/LICENSE.txt和powershell/ThirdPartyNotices.txt。
配套sources.zip含编译器源码、原生库汇编、构建脚本及修订材料；C251不随包分发。
'''
    (docs / '使用说明.txt').write_text(usage, encoding='utf8')
    for path in ['TRIAL_GATES.json', 'unfinished.json']:
        if (q / path).exists():
            shutil.copyfile(q / path, docs / ('r89-' + path))
    fixed = []
    for case in read(q / 'fixed-projects.json'):
        source = Path(case['project']).parent
        dest = component / 'examples/fixed' / case['name']
        shutil.copytree(source, dest)
        fixed.append(
            dict(name=case['name'], project='examples/fixed/' + case['name'] + '/project.json')
        )
    write(component / 'examples/fixed-projects.json', fixed)
    # 对应源码另行压缩，避免用户每次校验工具目录都扫描历史构建产物。
    source_archive = out / 'mx251-r89-sources.zip'
    with zipfile.ZipFile(source_archive, 'x', zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        roots = [
            (o / 'round87/source', 'compiler'),
            (o / 'round68/runtime-float-final', 'native-runtime/float'),
            (o / 'round68/runtime-integer-r32', 'native-runtime/integer'),
            (q / 'profile', 'qualified-profile'),
        ]
        for source, prefix in roots:
            assert source.is_dir(), source
            for p in sorted(source.rglob('*')):
                if (
                    p.is_file()
                    and not {'.git', '__pycache__'}.intersection(p.parts)
                    and p.suffix not in ['.o', '.exe', '.dll']
                ):
                    z.write(p, prefix + '/' + p.relative_to(source).as_posix())
        for parent in [o / 'round86', o / 'round87', q]:
            for pattern in ['*.patch', '*host*.py', 'REPRODUCE.txt']:
                for p in sorted(parent.glob(pattern)):
                    z.write(p, 'build-records/' + parent.name + '/' + p.name)
        z.writestr(
            'README.txt',
            'R89复用R87源码。compiler/为完整修改源码；native-runtime包含原生库源及对象；qualified-profile为实际入口。build-records保存构建及差异材料。许可证沿用各源文件和COPYING。\n',
        )
    write(
        component / 'provenance.json',
        dict(
            qualification='R89',
            compilerSourceCommit='b09075b6a93e6afe10645181e3aeff041ea37f87 plus R87 changes',
            compilerSha256=digest(t / 'bin/sdcc.exe'),
            profileLockSha256=digest(q / 'profile/profile.lock.json'),
            sourceArchive=source_archive.name,
            sourceArchiveSha256=digest(source_archive),
            qualifiedManifestSha256=digest(q / 'delivery-manifest.json'),
            compilerPayloadChanged=False,
            productName='MX251',
            upstream='SDCC 4.6.0 (modified fork)',
            hardwareScope='AI8051U-34K64 32-bit only',
        ),
    )
    manifest = dict(
        formatVersion=1,
        id='studiox.mx251',
        version='0.1.89',
        host='win-x64',
        compilerId='mx251-r89',
        purpose='mx251',
        displayName='MX251 R89（AI8051U 32位试用）',
        executables={
            'pwsh': 'powershell/pwsh.exe',
            'sdcc': 'sdcc/bin/sdcc.exe',
            'sdar': 'sdcc/bin/sdar.exe',
            'sdas251': 'sdcc/bin/sdas251.exe',
            'sdld': 'sdcc/bin/sdld.exe',
            'build251': 'profile/Build-251.ps1',
        },
        resourceDirectories={'sdcc': 'sdcc', 'profile': 'profile'},
        componentVersions={'mx251': 'R89', 'upstream': 'SDCC 4.6.0 (modified)'},
        sha256={
            p.relative_to(component).as_posix(): digest(p)
            for p in sorted(component.rglob('*'))
            if p.is_file()
        },
    )
    write(component / 'toolset.json', manifest)
    archive(component, out / 'studiox.mx251-0.1.89-win-x64.mcutoolchain')
    pack = out / 'pack-source'
    pack.mkdir()
    templates = []
    for model in ['small', 'large']:
        folder = pack / 'templates' / model
        folder.mkdir(parents=True)
        (folder / 'main.c').write_text(
            '''#include <stdint.h>

/* 内存心跳不假设底板LED接线；外设初始化需按实际原理图补充。 */
volatile __xdata uint32_t app_heartbeat = 0;

void main(void)
{
    for (;;)
    {
        ++app_heartbeat;
    }
}
''',
            encoding='utf8',
        )
        config = dict(
            schema=2,
            target='AI8051U-34K64-32',
            model=model,
            optimization='speed',
            backend='ordinary-wide',
            explicitAbi=False,
            standard='sdcc11',
            runtime='standard',
            defines=[],
            files=['src/main.c'],
            units=[
                dict(
                    name='main',
                    file='src/main.c',
                    backend='ordinary-wide',
                    stackAuto=True,
                    defines=[],
                )
            ],
            archives=[],
            link=['main', '@startup'],
        )
        write(folder / 'project.json', config)
        templates.append(
            dict(
                id=model,
                displayName='普通 C / ' + model,
                description='R89 普通 ABI 宽后端；明确登记源码，F7生成HEX。',
                entryFile='templates/' + model + '/main.c',
                mx251Configuration='templates/' + model + '/project.json',
            )
        )
    device = dict(
        id='AI8051U-34K64',
        displayName='AI8051U-34K64 · 32 位',
        architecture='mcs251',
        flashOrigin=0xFF0000,
        flashBytes=65536,
        ramOrigin=0x10000,
        ramBytes=32768,
        toolsetId='studiox.mx251',
        toolsetVersion='0.1.89',
        compilerId='mx251-r89',
        cpuFlags=[],
        defines=[],
        includeDirectories=[],
        sources=[],
        linkerScript='',
        compileOptions=[],
        linkOptions=[],
        templates=templates,
    )
    write(
        pack / 'manifest.json',
        dict(
            formatVersion=1,
            id='stc.ai8051u-mx251',
            version='0.1.89',
            displayName='AI8051U-34K64 / MX251 R89',
            vendor='STC',
            devices=[device],
        ),
    )
    (pack / 'README.txt').write_text(usage, encoding='utf8')
    shutil.copyfile(docs / '使用说明.txt', out / '使用说明.txt')
    write(
        out / 'preparation.json',
        dict(
            toolsetFiles=len(manifest['sha256']),
            toolsetArchiveSha256=digest(out / 'studiox.mx251-0.1.89-win-x64.mcutoolchain'),
            sourceArchiveSha256=digest(source_archive),
            compilerPayloadChanged=False,
            requiresUpdatedStudioX=True,
        ),
    )
    print(json.dumps(read(out / 'preparation.json'), ensure_ascii=False))


if __name__ == '__main__':
    main()
