"""Package a content-locked MX251 core and two StudioX format-1 device packs.

Only explicit local inputs are consumed. Installing the resulting packages is
performed separately through StudioX application services.
"""
from pathlib import Path
import argparse, hashlib, json, shutil, zipfile

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))

def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf8')

def copy_tree(source, target):
    shutil.copytree(source, target, ignore=shutil.ignore_patterns('.build', '__pycache__', '*.log'), dirs_exist_ok=True)

def write_text(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf8')

def prepare_analysis_header(path):
    # clangd 只能按宿主 ABI 解析 C；这些断言仍由 MX251 对实际固件执行。
    text=path.read_text(encoding='utf8')
    start='typedef char mx251_stc_char_is_8'
    end='typedef char mx251_stc_generic_is_24[(sizeof(unsigned char *) == 3) ? 1 : -1];'
    if '#if !defined(__clang__)' in text: return
    if start not in text or end not in text: raise ValueError('Unknown ABI assertion layout: '+str(path))
    text=text.replace(start, '/* clangd 不具备 MCS-251 ABI；目标宽度只由实际 MX251 编译检查。 */\n#if !defined(__clang__)\n'+start, 1)
    write_text(path, text.replace(end,end+'\n#endif',1))

def main():
    ap=argparse.ArgumentParser(description=__doc__)
    for name in ('distribution','stc-sdk','output'):
        ap.add_argument('--'+name, type=Path, required=True)
    ap.add_argument('--pack-version', default='0.2.171', help='StudioX device-pack revision; independent of compiler version')
    args=ap.parse_args()
    source=args.distribution.resolve(); stc=args.stc_sdk.resolve(); out=args.output.resolve()
    if out.exists(): raise FileExistsError(out)
    original=read(source/'core/core.json'); version=original['sourceVersion']
    major,minor=version.split('.'); component_version=f'{int(major)}.0.{int(minor)}'
    compiler_id='mx251-v'+version
    component=out/'component'; sdk=component/'sdk'; out.mkdir(parents=True)
    copy_tree(source/'core', sdk)
    copy_tree(source/'host/powershell',component/'powershell')
    copy_tree(source/'docs',component/'docs')
    for name in ('README.md','THIRD_PARTY.md','LICENSE','VERSION'):
        shutil.copy2(source/name,component/name)
    targets={}
    for profile in original['profiles']:
        lock=read(sdk/profile/'profile.lock.json')
        targets[lock['target']]=dict(profile=profile,profileLockSha256=digest(sdk/profile/'profile.lock.json'),compilerSha256=lock['compilerOverlay']['sha256'])
    sdk_manifest=dict(schema=1,name='MX251',version=version,status='development-not-LST',host='win-x64',
        buildChain=['CMake','Ninja','MX251'],projectModule='build/qualified/Project.cmake',buildScript='build/qualified/Build.ps1',
        externalHostTools=['PowerShell 7','CMake >= 3.25','Ninja'],targets=targets,
        sourceManifest='sources/compiler-files.json',sourceRepository=original['repository'],
        files={p.relative_to(sdk).as_posix():digest(p) for p in sorted(sdk.rglob('*')) if p.is_file()})
    write(sdk/'sdk.json',sdk_manifest)
    resources={'mx251-sdk':'sdk','sdcc':'sdk/toolchain',**{'profile-'+k:'sdk/'+v['profile'] for k,v in targets.items()}}
    manifest=dict(formatVersion=1,id='studiox.mx251',version=component_version,host='win-x64',compilerId=compiler_id,
        displayName=f'MX251 {version}（测试）',purpose='mx251',
        componentVersions={'mx251':version+' (test)','native-float':'66-member merged runtime'},
        executables={'pwsh':'powershell/pwsh.exe','sdcc':'sdk/profiles/AI8051U-34K64-32/compiler/sdcc.exe',
            'sdar':'sdk/toolchain/bin/sdar.exe','sdas251':'sdk/toolchain/bin/sdas251.exe','sdld':'sdk/toolchain/bin/sdld.exe',
            'build251':'sdk/build/qualified/Build.ps1'},resourceDirectories=resources,
        sha256={p.relative_to(component).as_posix():digest(p) for p in sorted(component.rglob('*')) if p.is_file()})
    write(component/'toolset.json',manifest)

    generated=stc/'mx251/generated'; compat=stc/'mx251/include'
    pack_version=args.pack_version
    records=[]
    for target,device_id,pack_id,title in (
        ('AI8051U-34K64-32','AI8051U-34K64','stc.ai8051u-mx251','AI8051U-32bit'),
        ('STC32G12K128','STC32G12K128','stc.stc32g-mx251','STC32G')):
        ai=target.startswith('AI8051U'); pack=out/'pack-sources'/pack_id
        pack.mkdir(parents=True)
        register='ai8051u.h' if ai else 'stc32g.h'
        for name in (register,):
            (pack/'include').mkdir(exist_ok=True);shutil.copy2(generated/target/'include'/name,pack/'include'/name)
        for name in ('mx251_stc_compat.h','intrins.h'):
            shutil.copy2(compat/name,pack/'include'/name)
        prepare_analysis_header(pack/'include/mx251_stc_compat.h')
        for name in ('COPYRIGHT-NOTICE.zh-CN.txt','SOURCE_MANIFEST.json'):
            shutil.copy2(stc/name,pack/name)
        defaults=dict(schema=2,target=target,model='small',optimization='speed',backend='ordinary-wide',
            explicitAbi=False,standard='sdcc11',runtime='standard',defines=[])
        write(pack/'templates/registers/mx251.json',defaults)
        write_text(pack/'templates/registers/main.c',f'''#include <stdint.h>
#include "{register}"

volatile __xdata uint32_t app_heartbeat;
volatile __xdata uint8_t app_input;

void main(void)
{{
    /* P2.0 高阻输入；不假定底板 LED 接线，不修改时钟或启用中断。 */
    EA = 0;
    P2M1 |= 0x01U;
    P2M0 &= 0xFEU;
    while (1)
    {{
        app_input = P2 & 0x01U;
        ++app_heartbeat;
    }}
}}
''')
        register_files={f'include/{n}':f'include/{n}' for n in (register,'mx251_stc_compat.h','intrins.h')}
        templates=[dict(id='registers',displayName='寄存器开发',description='直接配置 P2.0 高阻输入并更新内存心跳。',
            entryFile='templates/registers/main.c',files=register_files,replacesTemplates=['small','large'],
            mx251Configuration='templates/registers/mx251.json')]
        cmake='''cmake_minimum_required(VERSION 3.25)
project(mx251_firmware_project NONE)
set(MX251_FUNCTION_GC ON CACHE BOOL "Remove unreachable functions")
set(MX251_WIDE_VALUE_LIMIT 512 CACHE STRING "Wide values")
set(MX251_WIDE_NODE_LIMIT 2048 CACHE STRING "Wide nodes")
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)
target_sources(app INTERFACE src/main.c)
target_include_directories(app INTERFACE include)
'''
        if ai:
            # 保留已适配厂商驱动与 CMake 选件；寄存器模板不编译这些库源。
            lib=pack/'sdk/stc'; lib.mkdir(parents=True)
            source_manifest=read(stc/'mx251/generated-manifest.json')
            source_manifest['files']=[r for r in source_manifest['files'] if r['path'].startswith('generated/'+target+'/')]
            for collection in ('files','supportFiles'):
                for row in source_manifest[collection]:
                    src=stc/'mx251'/row['path']; dst=lib/row['path']
                    if digest(src)!=row['sha256']:raise ValueError('SDK content changed: '+row['path'])
                    dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(src,dst)
            write(lib/'generated-manifest.json',source_manifest)
            catalog=read(stc/'mx251/catalog.json');catalog['targets']={target:catalog['targets'][target]};write(lib/'catalog.json',catalog)
            for row in source_manifest['supportFiles']:
                if row['path']=='catalog.json': row['sha256']=digest(lib/'catalog.json')
            write(lib/'generated-manifest.json',source_manifest)
            for name in ('cmake/MX251StcSdk.cmake','tools/Check-Isr.ps1','README.zh-CN.txt'):
                dst=lib/name;dst.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(stc/'mx251'/name,dst)
                if name.endswith('.cmake'):
                    write_text(dst, dst.read_text(encoding='utf8').replace('${CMAKE_SOURCE_DIR}/.mx251-stc-sdk/', '${CMAKE_SOURCE_DIR}/.build/mx251-stc-sdk/'))
            prepare_analysis_header(lib/'include/mx251_stc_compat.h')
            # 选件副本属于构建缓存；原始 SDK 源码仍留在 device 中参与工程指纹。
            for row in source_manifest['supportFiles']:
                row['sha256']=digest(lib/row['path'])
            write(lib/'generated-manifest.json',source_manifest)
            write(pack/'templates/library/mx251.json',defaults)
            write_text(pack/'templates/library/library_config.h','#ifndef APP_LIBRARY_CONFIG_H\n#define APP_LIBRARY_CONFIG_H\n/* 库函数由 CMake 的 COMPONENTS 列表选择。 */\n#endif\n')
            write_text(pack/'templates/library/main.c','''#include "mx251_stc_sdk.h"

volatile __xdata uint32_t app_heartbeat;
volatile __xdata uint8_t app_input;
volatile __xdata uint8_t app_status;

void main(void)
{
    GPIO_InitTypeDef gpio;
    EA = 0;
    /* 使用厂商库配置高阻输入；不假定 LED、串口或 OLED 的板级接线。 */
    gpio.Pin = GPIO_Pin_0;
    gpio.Mode = GPIO_HighZ;
    app_status = GPIO_Inilize(GPIO_P2, &gpio);
    while (1)
    {
        if (app_status == SUCCESS)
        {
            app_input = P2 & GPIO_Pin_0;
            ++app_heartbeat;
        }
    }
}
''')
            templates.append(dict(id='library',displayName='库函数开发',description='STC 传统库的 MX251 源码适配；默认使用 GPIO，其他驱动由 CMake 选用。',
                entryFile='templates/library/main.c',files={'include/library_config.h':'templates/library/library_config.h'},
                mx251Configuration='templates/library/mx251.json'))
            cmake+='''if(EXISTS "${CMAKE_SOURCE_DIR}/include/library_config.h")
  include("${CMAKE_SOURCE_DIR}/device/sdk/stc/cmake/MX251StcSdk.cmake")
  # 此值用于库函数时间计算，必须与下载配置中的实际系统时钟一致。
  set(STC_CLOCK_HZ 40000000 CACHE STRING "Application system clock in Hz")
  mx251_stc_sdk(app TARGET AI8051U-34K64-32 CLOCK_HZ "${STC_CLOCK_HZ}" COMPONENTS GPIO)
  mx251_stc_firmware(app)
else()
  mx251_firmware(app)
endif()
'''
        else: cmake+='mx251_firmware(app)\n'
        write_text(pack/'CMakeLists.txt',cmake)
        device=dict(id=device_id,displayName=title+' / '+device_id,architecture='mcs251',flashOrigin=0xff0000,flashBytes=65536,
            ramOrigin=0x10000,ramBytes=32768 if ai else 8192,toolsetId='studiox.mx251',toolsetVersion=component_version,
            compilerId=compiler_id,cpuFlags=[],defines=[],includeDirectories=[],sources=[],linkerScript='',compileOptions=[],linkOptions=[],templates=templates,
            developmentComponents=[dict(id='studiox.mx251',version=component_version,compilerId=compiler_id),
                dict(id='studiox.cmake',version='4.4.0',compilerId='cmake-ninja',purpose='CMake工程管理')])
        write(pack/'manifest.json',dict(formatVersion=1,id=pack_id,version=pack_version,displayName=title+' / MX251 '+version+'（测试）',vendor='STC',devices=[device]))
        write_text(pack/'README.txt',f'''{title} / MX251 {version}（测试）
准确器件：{device_id}，32 位指令模式。
寄存器模板：完整适配寄存器头文件，P2.0 高阻输入和内存心跳，不主动驱动输出。
'''+('库函数模板：复用 STC 传统库适配源码，默认 GPIO；在 CMakeLists.txt 的 COMPONENTS 中添加所需驱动。提供 GPIO/CLOCK/DELAY/NVIC/TIMER/EXTI/UART/I2C/I2C_POLL/SPI/WDT/PWM/ADC/DMA/EEPROM 接入入口；各模块硬件资格以 SDK 记录为准。\n' if ai else '')+f'''
工具链组件版本：{component_version}；编译器身份：{compiler_id}；CMake/Ninja：studiox.cmake 4.4.0。
默认使用 standard 运行库与普通 C ABI；优化整数和数学库已随组件安装，选用 native-int/native-math-wide 时须同时匹配 explicit-wide/explicitAbi，不能混用对象 ABI。
当前构建 CODE 窗口为 0xFF0000 起 64 KiB；XDATA 分配上限为 {'28' if ai else '8'} KiB，EDATA 栈独立保留。本包描述可用构建窗口，不代表芯片全部物理 Flash 已开放。
模板不设置时钟；库函数的 STC_CLOCK_HZ 与下载页时钟需要一致。编译通过不等于实板验收。
版权和原始来源见 COPYRIGHT-NOTICE.zh-CN.txt、SOURCE_MANIFEST.json；SDK 是派生适配副本，保留原厂声明。
''')
        write(pack/'provenance.json',dict(version=version,target=target,registerSource='mx251/generated/'+target+'/include/'+register,
            registerSha256=digest(generated/target/'include'/register),compilerSha256=original['compiler']['sha256'],hardwareExecuted=False))
        hashes={p.relative_to(pack).as_posix():digest(p) for p in sorted(pack.rglob('*')) if p.is_file()}
        archive=out/f'{pack_id}-{pack_version}.mcupack'
        with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
            for name in hashes:z.write(pack/name,name)
            z.writestr('files.sha256.json',json.dumps(hashes,ensure_ascii=False,indent=2)+'\n')
        records.append(dict(id=pack_id,version=pack_version,archive=archive.name,device=device_id,templates=[t['id'] for t in templates],sha256=digest(archive)))
    write(out/'delivery.json',dict(version=version,componentVersion=component_version,compilerId=compiler_id,
        componentArchive=f'studiox.mx251-{component_version}-win-x64.mcutoolchain',packs=records))
    print(json.dumps(read(out/'delivery.json'),ensure_ascii=False),flush=True)

if __name__=='__main__': main()
