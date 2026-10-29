"""生成独立STC251格式1器件包的源目录，不修改编译器组件或既有器件包。"""

from pathlib import Path
import argparse
import hashlib
import json
import shutil


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--reference-manual', type=Path, required=True)
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    out = args.output.resolve()
    if out.exists():
        raise FileExistsError('Use a new pack source directory')
    manual = args.reference_manual.resolve()
    manual_hash = hashlib.sha256(manual.read_bytes()).hexdigest()
    reference_path = root / 'tools/data/ai8051u-registers.json'
    reference = json.loads(reference_path.read_text(encoding='utf8'))
    if manual_hash != reference['manual']['sha256']:
        raise ValueError('Reference manual differs from the audited register inventory')
    from importlib.util import spec_from_file_location, module_from_spec

    spec = spec_from_file_location('ai8051u_header', root / 'tools/Generate-Ai8051UHeader.py')
    generator = module_from_spec(spec)
    spec.loader.exec_module(generator)
    if (root / 'examples/packs/stc.stc251/include/AI8051U.h').read_text(
        encoding='utf8'
    ) != generator.generate(reference_path):
        raise ValueError('Regenerate AI8051U.h before packaging')
    shutil.copytree(root / 'examples/packs/stc.stc251', out)
    shutil.copyfile(reference_path, out / 'register-reference.json')
    templates = []
    for model in ['small']:
        configuration = 'templates/registers/project.json'
        write(
            out / configuration,
            dict(
                schema=2,
                target='AI8051U-34K64-32',
                model=model,
                optimization='speed',
                backend='ordinary-wide',
                explicitAbi=False,
                standard='sdcc11',
                runtime='standard',
                defines=['MX251_IRC_HZ=40000000', 'MX251_DELAY_TICKS=9999'],
            ),
        )
        templates.append(
            dict(
                id='registers',
                displayName='寄存器开发',
                replacesTemplates=['small', 'large'],
                description='直接操作寄存器：P2.0高阻输入与内存心跳；无HAL、SPL或RTOS。',
                entryFile='templates/registers/main.c',
                files={
                    p: p
                    for p in ['include/AI8051U.h', 'include/AI8051U_Delay.h', 'src/AI8051U_Delay.c']
                },
                mx251Configuration=configuration,
            )
        )
    device = dict(
        id='AI8051U-34K64',
        displayName='AI8051U-34K64-32Bit（STC80251）',
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
        developmentComponents=[
            dict(id='studiox.mx251', version='0.1.89', compilerId='mx251-r89'),
            dict(
                id='studiox.cmake',
                version='4.4.0',
                compilerId='cmake-ninja',
                purpose='CMake工程管理',
            ),
        ],
    )
    write(
        out / 'manifest.json',
        dict(
            formatVersion=1,
            id='stc.stc251',
            version='0.1.4',
            displayName='STC251',
            vendor='STC',
            devices=[device],
        ),
    )
    write(
        out / 'provenance.json',
        dict(
            source='AI8051U系列技术手册',
            manufacturer='STC',
            referenceFile=manual.name,
            referenceSha256=manual_hash,
            pdfPages=[586, '699-719', 1697, 1702, '1715-1717'],
            registerReference='register-reference.json',
            headerReference=reference['headerReference'],
            scope='完整STC寄存器名称集合及手册RTC别名：116个SFR、112个位、676个XFR名称；含USB间接地址、中断号和数学命令常量。无新增实板验收。',
            compiler='MX251 R89 / studiox.mx251 0.1.89',
            compilerChanged=False,
        ),
    )
    print(out)


if __name__ == '__main__':
    main()
