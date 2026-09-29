"""Offline StudioX format-1 recipes from pinned official DFP/CMSIS sources.

No tools are installed; no hardware access. Unknown mappings fail closed.
"""
import argparse, fnmatch, hashlib, json, re, shutil, subprocess, zipfile
import xml.etree.ElementTree as ET
from collections import defaultdict
from pathlib import Path

VERSION = '0.1.0'
ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / 'examples/packs/arm32-expansion'

def sha(data): return hashlib.sha256(data).hexdigest()
def write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(text, encoding='utf-8', newline='\n')
def jsonfile(path, data): write(path, json.dumps(data, ensure_ascii=False, indent=2)+'\n')
def cleanpath(value):
    value=value.replace('\\','/')
    while value.startswith('./'):value=value[2:]
    if value.startswith('/') or ':' in value or '..' in value.split('/'): raise ValueError('Invalid source path: '+value)
    return value

class Archive:
    def __init__(self, path, expected, github=False):
        if sha(path.read_bytes()) != expected: raise ValueError('Source hash changed: '+str(path))
        with zipfile.ZipFile(path) as z:
            self.files={cleanpath(n.filename):z.read(n) for n in z.infolist() if not n.is_dir()}
        if github:
            self.files={n.split('/',1)[1]:v for n,v in self.files.items() if '/' in n}
        self.names={n.lower():n for n in self.files}
    def resolve(self, name):
        name=cleanpath(name)
        if name in self.files:return name
        if name.lower() in self.names:return self.names[name.lower()]
        raise FileNotFoundError(name)
    def text(self,name):return self.files[self.resolve(name)].decode('utf-8-sig',errors='replace').replace('\r','')

def catalog(pdsc):
    def walk(node, ancestors):
        chain=ancestors+[node]
        children=[c for c in node if c.tag in ('family','subFamily','device','variant')]
        # DFP 基础型号优先；仅当子变体提供不同编译定义时才展开变体，避免把封装占位符当成订货料号。
        if node.tag=='device':
            variants=node.findall('variant')
            if variants and any(v.find('compile') is not None for v in variants):
                for v in variants:yield chain+[v]
            else:yield chain
            return
        for child in children:yield from walk(child,chain)
    yield from walk(pdsc.find('devices'),[])

def attrs(chain, tag):
    values={}
    for node in chain:
        for child in node.findall(tag):
            if child.get('Pname'):raise ValueError('Multi-core processor-specific declaration requires dedicated template')
            values.update(child.attrib)
    return values

def memories(chain):
    result={}
    for node in chain:
        for child in node.findall('memory'):
            if child.get('Pname'):raise ValueError('Multi-core memory requires dedicated template')
            result[child.get('id',child.get('name'))]=dict(child.attrib)
    return list(result.values())

def layout(chain):
    ms=memories(chain)
    rom=[m for m in ms if m.get('startup','').lower() in ('1','true')]
    if len(rom)!=1:raise ValueError('Need one explicit startup Flash region')
    flash=rom[0]; f=int(flash['start'],0); size=int(flash['size'],0)
    # 只合并明确连续的主 Flash bank；不把 EEPROM、配置区或地址空洞算进可链接空间。
    banks=[m for m in ms if m is not flash and 'flash_bank' in m.get('name','').lower()]
    for bank in sorted(banks,key=lambda m:int(m['start'],0)):
        if int(bank['start'],0)!=f+size:raise ValueError('Discontinuous Flash banks require multiple-region linker model')
        size+=int(bank['size'],0)
    ram=[m for m in ms if m.get('default','1')=='1' and (m.get('id','').startswith('IRAM') or ('w' in m.get('access','') and 'p' not in m.get('access','') and 0x10000000<=int(m['start'],0)<0x40000000))]
    ram=[m for m in ram if not any(x in m.get('name','').upper() for x in ('IAP','FLEX','BACKUP'))]
    if not ram:raise ValueError('No explicit default RAM')
    chosen=sorted(ram,key=lambda m:(0 if m.get('id')=='IRAM1' else 1,int(m['start'],0)))[0]
    return f,size,int(chosen['start'],0),int(chosen['size'],0),ms

class Conditions:
    def __init__(self, root, chain):
        self.conditions={n.get('id'):n for n in root.findall('./conditions/condition')}
        self.values={}
        for node in chain:self.values.update(node.attrib)
        self.values.update(attrs(chain,'processor'))
        self.values['Tcompiler']='GCC' if root.findtext('vendor')=='Microchip' else 'ARMCC'
    def matches(self, node, seen):
        for key,value in node.attrib.items():
            if key=='condition':
                if not self.test(value,seen):return False
            elif key.startswith('D') or key=='Tcompiler':
                candidates=[self.values.get(key,'')]
                if key=='Dname' and self.values.get('Dvariant'):candidates.append(self.values['Dvariant'])
                if not any(fnmatch.fnmatchcase(v,value) for v in candidates):return False
        return True
    def test(self, name, seen=()):
        if not name:return True
        if name in seen:raise ValueError('Recursive DFP condition: '+name)
        if name not in self.conditions:raise ValueError('Missing DFP condition: '+name)
        node=self.conditions[name];seen=seen+(name,)
        requires=node.findall('require');accepts=node.findall('accept');denies=node.findall('deny')
        return all(self.matches(n,seen) for n in requires) and (not accepts or any(self.matches(n,seen) for n in accepts)) and not any(self.matches(n,seen) for n in denies)

def select_files(root, chain, source):
    condition=Conditions(root,chain);selected=[]
    for component in root.findall('.//component'):
        if component.get('Cgroup') not in ('Startup','System'):continue
        if not condition.test(component.get('condition')):continue
        for f in component.findall('./files/file'):
            name=cleanpath(f.get('name',''))
            if not condition.test(f.get('condition')):continue
            if ('startup' in name.lower() or Path(name).name.lower().startswith('system_')) and name.lower().endswith(('.s','.c')):
                selected.append(source.resolve(name))
    starts=sorted(set(n for n in selected if 'startup' in n.lower()))
    systems=sorted(set(n for n in selected if Path(n).name.lower().startswith('system_') and n.endswith('.c')))
    if not systems:
        # NXP 的 system 文件是 Device:C Header 组件，名称与 DFP 家族一致。
        systems=[n for n in source.files if Path(n).name.lower().startswith('system_') and n.endswith('.c') and not n.startswith(('Boards/','Examples/'))]
    if len(starts)!=1 or len(systems)!=1:raise ValueError('Ambiguous startup/system: '+str((starts,systems)))
    return starts[0],systems[0]

def vectors(text):
    start=re.search(r'(?m)^\s*(?:__Vectors|g_pfnVectors|__vector_table)\s*(?::|\s+DCD)',text)
    if not start:raise ValueError('Vendor vector table label not found')
    block=text[start.start():]
    end=re.search(r'(?m)^\s*(?:__Vectors_End|__Vectors_Size|\.size\s+(?:g_pfnVectors|__Vectors)|\.text|\.section\s+\.(?:text|FlashConfig)|AREA\s+\|?\.text)',block)
    if end:block=block[:end.start()]
    result=[]
    for line in block.splitlines():
        line=re.split(r';|/\*|//',line)[0]
        m=re.search(r'(?:\bDCD|\.word|\.long)\s+([^\s,]+)',line)
        if m:result.append(m.group(1))
    if len(result)<16 or result[1]!='Reset_Handler':raise ValueError('Invalid vector table '+str(result[:3]))
    if not all(re.fullmatch(r'[A-Za-z_][A-Za-z_0-9]*|0|0x[0-9A-Fa-f]+',v) for v in result[1:]):raise ValueError('Vector expression requires manual review')
    return result

def arm_reset(text):
    """Preserve vendor reset instructions, including errata/POR/RAM setup; reject unknown directives."""
    m=re.search(r'(?m)^\s*Reset_Handler\s+PROC\b([\s\S]*?)\bENDP',text)
    if not m:
        # 一些标为 ARMCC 的 DFP 实际使用 GNU 语法；仅接受有明确结尾的复位函数。
        m=re.search(r'(?m)^Reset_Handler:\s*\n([\s\S]*?)(?=^\s*\.pool|^//\s*; Dummy Exception|^\s*//\s*; Dummy Exception)',text)
        if not m:return None
    result=[]
    for name,value in re.findall(r'(?m)^\s*(\w+)\s+EQU\s+(0x[0-9a-fA-F]+|\d+)\s*(?:;.*)?$',text):
        result.append(f'.equ {name}, {value}')
    for raw in m.group(1).splitlines():
        line=re.sub(r'/\*.*?\*/','',raw).split(';')[0].strip()
        if not line:continue
        op=line.split()[0].upper()
        if op=='.EQU':result.append(line);continue
        if op in ('IMPORT','EXPORT'):continue
        if op=='REQUIRE' and line.split()[1]=='FlashConfig':continue # linker keeps the reviewed configuration word
        if op=='IF':
            cond=re.fullmatch(r'IF\s+:LNOT:\s*:DEF:\s*(\w+)',line)
            if not cond:raise ValueError('Reset condition needs manual adaptation: '+line)
            result.append('#ifndef '+cond[1]);continue
        if op=='ENDIF':result.append('#endif');continue
        if len(line.split())==1 and op not in ('NOP',):result.append(line+':');continue
        if not re.fullmatch(r'(LDR|STR|MOVS?|ADDS?|SUBS?|CMP|BLS|BNE|BEQ|B|BL|BLX|BX|MSR|CPSID|CPSIE|ORRS?|ANDS?|BICS?|LSRS?|NOP)',op):
            raise ValueError('Reset instruction needs manual adaptation: '+line)
        line=re.sub(r'\b__main\b','StudioX_CRuntime',line)
        line=re.sub(r'\b__initial_sp\b','_estack',line)
        # ARMASM accepts the Thumb-1 ADD Rd, PC, #0 spelling; GAS requires ADR.
        line=re.sub(r'ADD\s+(R\d+),\s*PC,\s*#0',r'ADD \1, PC, #0',line)
        result.append('    '+line)
    if not any('StudioX_CRuntime' in s for s in result):raise ValueError('Reset does not enter the C runtime')
    return '.syntax unified\n.thumb\n.section .text.Reset_Handler,"ax",%progbits\n.global Reset_Handler\n.type Reset_Handler,%function\n.thumb_func\nReset_Handler:\n'+'\n'.join(result)+'\n.ltorg\n.size Reset_Handler, .-Reset_Handler\n'

def startup_text(entries, lpc=False, vendor_reset=False):
    handlers=sorted(set(entries[1:]) - {'0','Reset_Handler','Default_Handler'})
    handlers=[h for h in handlers if not h.startswith('0x')]
    decl='\n'.join(f'void {h}(void) __attribute__((weak, alias("Default_Handler")));' for h in handlers)
    slots=['(uintptr_t)&_estack']+[v if v.startswith('0x') or v=='0' else '(uintptr_t)'+v for v in entries[1:]]
    if lpc:slots[7]='(uintptr_t)&__valid_user_code_checksum'
    result='''/* 依据厂商启动文件逐项转写向量名称/顺序；原文件及 SHA-256 随包保存。 */
#include <stdint.h>
extern uint32_t _estack, _sidata, _sdata, _edata, _sbss, _ebss, __valid_user_code_checksum;
extern void SystemInit(void);
extern void __libc_init_array(void);
extern int main(void);
void Reset_Handler(void);
void Default_Handler(void) { for (;;) { __asm volatile ("nop"); } }
'''+decl+'''
__attribute__((used, section(".isr_vector")))
const uintptr_t __Vectors[] = {
    '''+',\n    '.join(slots)+'''
};
void Reset_Handler(void)
{
    SystemInit();
    uint32_t *source = &_sidata;
    for (uint32_t *destination = &_sdata; destination < &_edata;) *destination++ = *source++;
    for (uint32_t *destination = &_sbss; destination < &_ebss;) *destination++ = 0;
    __libc_init_array();
    (void)main();
    for (;;) { __asm volatile ("nop"); }
}
'''
    if vendor_reset:result=result.replace('void Reset_Handler(void)\n{\n    SystemInit();','void StudioX_CRuntime(void)\n{')
    return result

def linker(flash,flen,ram,rlen,lpc=False,kinetis=False,entries=None):
    reserve=min(1024,max(256,rlen//4))
    check=''
    if lpc:
        names=['_estack']+[f'({n} | 1)' for n in entries[1:7] if n!='0']
        check='__valid_user_code_checksum = 0 - ('+' + '.join(names)+');\n'
    config='  .flash_config ORIGIN(FLASH) + 0x400 : { LONG(0xFFFFFFFF) LONG(0xFFFFFFFF) LONG(0xFFFFFFFF) LONG(0xFFFFFFFE) } > FLASH\n' if kinetis else ''
    return f'''ENTRY(Reset_Handler)
MEMORY
{{
  FLASH (rx) : ORIGIN = 0x{flash:08x}, LENGTH = 0x{flen:x}
  RAM (rwx) : ORIGIN = 0x{ram:08x}, LENGTH = 0x{rlen:x}
}}
_estack = ORIGIN(RAM) + LENGTH(RAM);
__StackTop = _estack;
__StackLimit = _estack - {reserve};
{check}SECTIONS
{{
  .isr_vector : {{ _sfixed = .; KEEP(*(.isr_vector)) KEEP(*(.vectors)) }} > FLASH
{config}  .text : {{ *(.text*) *(.rodata*) KEEP(*(.init)) KEEP(*(.fini)) . = ALIGN(4); }} > FLASH
  .ARM.extab : {{ *(.ARM.extab*) }} > FLASH
  .ARM.exidx : {{ __exidx_start = .; *(.ARM.exidx*) __exidx_end = .; }} > FLASH
  .preinit_array : {{ PROVIDE_HIDDEN(__preinit_array_start = .); KEEP(*(.preinit_array*)) PROVIDE_HIDDEN(__preinit_array_end = .); }} > FLASH
  .init_array : {{ PROVIDE_HIDDEN(__init_array_start = .); KEEP(*(SORT(.init_array.*))) KEEP(*(.init_array*)) PROVIDE_HIDDEN(__init_array_end = .); }} > FLASH
  .fini_array : {{ PROVIDE_HIDDEN(__fini_array_start = .); KEEP(*(SORT(.fini_array.*))) KEEP(*(.fini_array*)) PROVIDE_HIDDEN(__fini_array_end = .); }} > FLASH
  .data : {{ . = ALIGN(4); _sdata = .; _srelocate = .; *(.data*) *(.ramfunc*) . = ALIGN(4); _edata = .; _erelocate = .; }} > RAM AT> FLASH
  _sidata = LOADADDR(.data); _etext = _sidata; _efixed = _sidata;
  .bss (NOLOAD) : {{ . = ALIGN(4); _sbss = .; _szero = .; *(.bss*) *(COMMON) . = ALIGN(4); _ebss = .; _ezero = .; }} > RAM
  .noinit (NOLOAD) : {{ *(.noinit*) . = ALIGN(8); _end = .; end = .; __end__ = .; }} > RAM
  _sstack = __StackLimit; __HeapBase = _end; __HeapLimit = __StackLimit;
  ASSERT(_end <= __StackLimit, "RAM data collides with reserved stack")
  /DISCARD/ : {{ *(.note.GNU-stack) }}
}}
'''

def copy_file(stage, name, content):
    p=stage/name;p.parent.mkdir(parents=True,exist_ok=True)
    if p.exists() and p.read_bytes()!=content:raise ValueError('Content collision: '+name)
    p.write_bytes(content)

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--sources',type=Path,required=True);p.add_argument('--cmsis',type=Path,required=True)
    p.add_argument('--output',type=Path,required=True);p.add_argument('--match',default='');p.add_argument('--pack',default='')
    a=p.parse_args()
    locked=json.loads((RECIPE/'sources.lock.json').read_text(encoding='utf-8'))
    for name,expected in locked['cmsisCore']['filesSha256'].items():
        if sha((a.cmsis/cleanpath(name)).read_bytes())!=expected:raise ValueError('CMSIS Core differs from reviewed source: '+name)
    if a.output.exists():raise ValueError('Use a new output directory')
    a.output.mkdir(parents=True,exist_ok=True)
    downloads=json.loads((a.sources/'downloads/downloads.json').read_text(encoding='utf-8'))
    st_records=json.loads((a.sources/'st-cmsis/sources.json').read_text(encoding='utf-8'))
    hashes={r['file']:r['sha256'] for r in locked['dfp']+locked['stCmsis']}
    for record in downloads+st_records:
        if hashes.get(record['file'])!=record['sha256']:raise ValueError('Source is not in the reviewed lock: '+record['file'])
    st={r['family']:(r,Archive(a.sources/'st-cmsis'/r['file'],r['sha256'],True)) for r in st_records}
    rejected=[];index=[]
    for record in downloads:
        if a.match and not re.search(a.match,record['name']):continue
        if record['status']!='downloaded':continue
        try:source=Archive(a.sources/'downloads'/record['file'],record['sha256'])
        except (ValueError,zipfile.BadZipFile) as error:
            rejected.append(dict(source=record['file'],device='*',reason=str(error)));continue
        pdscname=next(n for n in source.files if n.endswith('.pdsc'));root=ET.fromstring(source.files[pdscname])
        groups=defaultdict(list)
        for chain in catalog(root):
            identity=chain[-1].get('Dvariant',chain[-1].get('Dname'))
            try:
                if identity.startswith('Generic'):raise ValueError('Generic placeholder is not a concrete device')
                process=attrs(chain,'processor');core=process['Dcore'].lower().replace('+','plus')
                if core not in ('cortex-m0','cortex-m0plus','cortex-m3','cortex-m4','cortex-m7'):raise ValueError('Dedicated security/multicore profile required: '+core)
                cpu=['-mcpu='+core,'-mthumb','-mfloat-abi=soft']
                compile=attrs(chain,'compile');defines=[d.strip() for d in re.split(r'[,;\s]+',compile.get('define','')) if d.strip()]
                if record['vendor']=='Keil' and record['name'].startswith('STM32'):
                    family=record['name'][5:7].lower();srcrecord,sdk=st[family];define=defines[0]
                    header='Include/stm32'+family+'xx.h'
                    exact=[name for name in re.findall(r'defined\s*\(\s*(\w+)\s*\)',sdk.text(header)) if name.lower()==define.lower()]
                    if exact:defines[0]=define=exact[0]
                    startup=sdk.resolve('Source/Templates/gcc/startup_'+define.lower()+'.s')
                    system=sdk.resolve('Source/Templates/system_stm32'+family+'xx.c')
                    subfamily=next(n.get('DsubFamily') for n in reversed(chain) if n.get('DsubFamily'))
                    prefix='studiox';vendor='STMicroelectronics'
                else:
                    sdk=source;srcrecord=record;startup,system=select_files(root,chain,sdk)
                    if 'header' not in compile and record['vendor']=='Geehy':
                        # 部分原厂 DFP 未写 compile 节点，从已匹配 system 文件的明确 include 取头名。
                        names=re.findall(r'#include\s+"(apm32[^"]+\.h)"',sdk.text(system))
                        candidates=[n for n in sdk.files if n.startswith('Device/') and Path(n).name in names]
                        if len(candidates)!=1:raise ValueError('System header is not uniquely identified')
                        header=candidates[0]
                        family=next(n.get('DsubFamily') for n in reversed(chain) if n.get('DsubFamily'))
                        macro=family+'xx'
                        if re.search(r'defined\s*\(\s*'+macro+r'\s*\)',sdk.text(header)):defines.append(macro)
                    else:header=sdk.resolve(compile['header'])
                    subfamily=next((n.get('DsubFamily') for n in reversed(chain) if n.get('DsubFamily')),None)
                    if record['vendor']=='NordicSemiconductor':subfamily=identity.split('_')[0];prefix='nordic';vendor='Nordic Semiconductor'
                    elif record['vendor']=='Microchip':subfamily='SAMD21' if 'SAMD21' in identity else 'SAMD51';prefix='microchip';vendor='Microchip'
                    elif record['vendor'] in ('Keil','NXP'):subfamily=subfamily or record['name'].removesuffix('_DFP');prefix='nxp';vendor='NXP'
                    else:prefix={'HDSC':'hdsc','MindMotion':'mindmotion','Geehy':'geehy','NSING':'nsing','Nuvoton':'nuvoton','ArteryTek':'artery'}[record['vendor']];vendor=record['vendor'];subfamily=subfamily or record['name'].removesuffix('_DFP')
                sdk.resolve(header)
                if identity.startswith('APM32F465'):defines.append('STUDIOX_CLOCK_DECLARED_BY_DEVICE=1')
                if vendor=='ArteryTek':
                    # DFP 使用前导短横线排序；型号和编译宏均保留厂商明确声明的料号。
                    if identity.startswith('-') and identity[1:].replace('-','_') in defines:identity=identity[1:]
                    if identity.startswith('AT32F490'):raise ValueError('Vendor reset unconditionally initializes board-specific QSPI PSRAM; requires dedicated board template')
                    if identity.startswith('AT32F421') and defines[0] not in re.findall(r'defined\s*\(\s*(\w+)\s*\)',sdk.text(header)):
                        raise ValueError('DFP model macro has no matching device header branch: '+defines[0])
                    defines+=['SystemCoreClock=system_core_clock','SystemCoreClockUpdate=system_core_clock_update','STUDIOX_CLOCK_DECLARED_BY_DEVICE=1']
                custom_clock={'N32G003':('SystemCoreClockFrequency','System_Core_Clock_Frequency_Update'),'N32G430':('SystemClockFrequency','System_Clock_Frequency_Update')}
                clock=next((v for k,v in custom_clock.items() if identity.startswith(k)),None)
                if clock:defines += ['SystemCoreClock='+clock[0],'SystemCoreClockUpdate='+clock[1]]
                elif vendor!='ArteryTek' and not re.search(r'\bvoid\s+SystemCoreClockUpdate\s*\(',sdk.text(system)):
                    defines.append('STUDIOX_CLOCK_FROM_SYSTEM_INIT=1')
                if record['vendor']=='Geehy' and record['name'].startswith('APM32F0'):
                    macro=re.match(r'APM32F\d{3}',identity)[0]
                    if not re.search(r'defined\s*\(\s*'+macro+r'\s*\)',sdk.text(header)) or macro in ('APM32F070','APM32F071'):
                        raise ValueError('DFP lists model but matching register header is absent: '+macro)
                    defines.append(macro)
                if record['vendor']=='NSING':defines=[d for d in defines if d!='USE_STDPERIPH_DRIVER']
                if record['vendor']=='MindMotion':subfamily=re.match(r'MM32F\d{3,4}',identity)[0]
                f,flen,r,rlen,ms=layout(chain)
                if identity.startswith('nRF91'):raise ValueError('Cellular SDK/secure boot profile is out of generic MCU scope')
                if vendor=='Nuvoton' and startup.endswith('.c'):raise ValueError('CMSIS C startup requires a dedicated protected-reset/runtime adapter')
                if vendor=='Nuvoton' and subfamily.upper() in ('M479','N32F030','NM1120','NM1200','NM1230','NM1240','NM1320','NM1330','NM1500','NM1810','NM1820','TF5100'):
                    raise ValueError('Official DFP is missing required family register/StdDriver headers; no cross-family substitution')
                vectors_list=None if startup.lower().endswith('.c') else vectors(sdk.text(startup))
                reset=arm_reset(sdk.text(startup)) if vectors_list else None
                if vectors_list and reset is None and vendor!='STMicroelectronics':raise ValueError('Unrecognized reset procedure requires dedicated conversion')
                groups[prefix+'.'+re.sub('[^a-z0-9]+','-',subfamily.lower()).strip('-')].append(dict(id=identity,core=core,cpu=cpu,defines=defines,header=header,startup=startup,system=system,sdk=sdk,source=srcrecord,flash=f,flashBytes=flen,ram=r,ramBytes=rlen,memories=ms,vectors=vectors_list,reset=reset,vendor=vendor,subfamily=subfamily))
            except Exception as error:
                rejected.append(dict(source=record['file'],device=identity,reason=str(error)))
        for packid,rows in groups.items():
            if a.pack and not re.search(a.pack,packid):continue
            stage=a.output/'staging'/packid
            if stage.exists():raise ValueError('Staging already exists: '+str(stage))
            stage.mkdir(parents=True)
            copy_file(stage,'vendor/'+Path(pdscname).name,source.files[pdscname])
            for path in sorted((a.cmsis/'CMSIS/Core/Include').rglob('*.h')):
                copy_file(stage,'sdk/core/'+path.relative_to(a.cmsis/'CMSIS/Core/Include').as_posix(),path.read_bytes())
            copy_file(stage,'licenses/ARM-CMSIS-LICENSE', (a.cmsis/'LICENSE').read_bytes())
            copy_file(stage,'vendor/ARM.CMSIS.pdsc',(a.cmsis/'ARM.CMSIS.pdsc').read_bytes())
            for name,data in source.files.items():
                if Path(name).name.lower().startswith(('license','licence','copying')):copy_file(stage,'licenses/dfp/'+name,data)
            devices=[];evidence=[]
            for row in rows:
                sdk=row['sdk'];is_st=row['vendor']=='STMicroelectronics';header=row['header']
                # 只携带所选器件目录的原厂头文件；避免同名 NuMicro.h/system.h 混入其他子系列。
                header_dir=str(Path(header).parent).replace('\\','/')
                if row['vendor']=='Nuvoton':sdk_prefix='/'.join(header.split('/')[:2])+'/'
                elif row['vendor']=='Microchip':sdk_prefix=header.split('/')[0]+'/'
                elif row['vendor']=='MindMotion':sdk_prefix='/'.join(header.split('/')[:2])+'/'
                else:sdk_prefix=''
                includes=['sdk/core']
                for name,data in sdk.files.items():
                    if name.startswith(('Boards/','Documentation/','Examples/','Tests/','Projects/')):continue
                    if sdk_prefix and not name.startswith(sdk_prefix):continue
                    if name.lower().endswith('.h') and not re.search(r'(?:^|/)(?:Core|CMSIS)/Include/core_',name,re.I):
                        copy_file(stage,'sdk/vendor/'+name,data)
                        directory=('sdk/vendor/'+str(Path(name).parent).replace('\\','/')).removesuffix('/.')
                        if directory not in includes:includes.append(directory)
                    if Path(name).name.lower().startswith(('license','licence','copying')):copy_file(stage,'licenses/sdk/'+name,data)
                # 明确的设备头目录在前，通用 CMSIS 目录不使用旧 SDK 内 ARMCC 专属副本。
                primary_include=('sdk/vendor/'+header_dir).removesuffix('/.')
                includes=[primary_include]+[d for d in includes if d!=primary_include]
                system='sdk/vendor/'+row['system'];system_content=sdk.files[row['system']]
                if row['id'].startswith('HC32F072'):
                    original=system_content
                    old=b'unsigned int SystemCoreClock = 4000000;'
                    if system_content.count(old)!=1:raise ValueError('HC32F072 reviewed declaration changed')
                    system_content=system_content.replace(old,b'uint32_t SystemCoreClock = 4000000;')
                    copy_file(stage,'vendor/original/'+row['system'],original)
                    row['adaptations']=['HC32F072 SystemCoreClock definition uses uint32_t to match vendor system header on GCC/newlib; original preserved.']
                copy_file(stage,system,system_content)
                startup_id=sha(sdk.files[row['startup']])[:16]
                extra=[]
                if row['vendor']=='Nuvoton':
                    for name in sdk.files:
                        if name.startswith(sdk_prefix) and re.search(r'/StdDriver/src/(clk|sys)\.c$',name,re.I):
                            copy_file(stage,'sdk/vendor/'+name,sdk.files[name]);extra.append('sdk/vendor/'+name)
                if row['id'].startswith('N32G003'):
                    for name in sdk.files:
                        if name.endswith('/n32g003_rcc.c'):
                            copy_file(stage,'sdk/vendor/'+name,sdk.files[name]);extra.append('sdk/vendor/'+name)
                if row['vendor']=='ArteryTek':
                    for name in sdk.files:
                        if name.endswith('_crm.c'):
                            copy_file(stage,'sdk/vendor/'+name,sdk.files[name]);extra.append('sdk/vendor/'+name)
                lpc=row['id'].startswith('LPC');kinetis=row['id'].startswith('MK')
                if row['vectors'] is None or is_st:
                    start='sdk/vendor/'+row['startup'];copy_file(stage,start,sdk.files[row['startup']])
                else:
                    start='system/startup_'+startup_id+'.c';write(stage/start,startup_text(row['vectors'],lpc,bool(row['reset'])))
                    if row['reset']:
                        resetfile='system/reset_'+startup_id+'.S';write(stage/resetfile,row['reset']);extra.append(resetfile)
                    copy_file(stage,'vendor/startup/'+Path(row['startup']).name,sdk.files[row['startup']])
                key=re.sub('[^A-Za-z0-9_]','_',row['id'])
                ld='linker/'+key+'.ld';write(stage/ld,linker(row['flash'],row['flashBytes'],row['ram'],row['ramBytes'],lpc,kinetis,row['vectors']))
                config='templates/'+key+'/StudioX_Device.h'
                write(stage/config,'#pragma once\n#include "'+Path(header).name+'"\n')
                if row['vendor']=='HDSC' and row['id'].startswith('HC32F072'):
                    # 官方 DFP 的系统文件引用通用名，却只附带按封装命名的头文件。
                    alias='config/'+key+'/hc32F072.h';write(stage/alias,'#pragma once\n#include "'+Path(header).name+'"\n')
                    includes.insert(0,'config/'+key)
                devices.append(dict(id=re.sub(r'[^A-Za-z0-9._-]','_',row['id']),displayName=row['id'],architecture='arm',flashOrigin=row['flash'],flashBytes=row['flashBytes'],ramOrigin=row['ram'],ramBytes=row['ramBytes'],toolsetId='arm.gnu',toolsetVersion='1.0.0',compilerId='arm-gnu-15.2.rel1',cpuFlags=row['cpu'],defines=row['defines'],includeDirectories=includes,sources=[start,system,'system/StudioX_System.c']+extra,linkerScript=ld,compileOptions=['-ffunction-sections','-fdata-sections','-ffreestanding'],linkOptions=['-nostartfiles','--specs=nano.specs','--specs=nosys.specs','-Wl,--gc-sections'],templates=[dict(id='cmsis',displayName='CMSIS · C 基础工程',description='原厂寄存器头文件、启动和系统时钟支持；离线编译验证，不包含实板验收或无线协议栈。',entryFile='templates/main.c',files={'include/StudioX_Device.h':config,'include/StudioX_System.h':'templates/StudioX_System.h'},build={'defines':[],'includeDirectories':[],'sources':[],'compileOptions':[],'linkOptions':[]})]))
                evidence.append({k:v for k,v in row.items() if k not in ('sdk','source','vectors','cpu')} | {'source':row['source'],'startupSha256':sha(sdk.files[row['startup']]),'vectors':row['vectors']})
            for name in ['main.c','StudioX_System.h']:copy_file(stage,'templates/'+name,(RECIPE/name).read_bytes())
            copy_file(stage,'system/StudioX_System.c',(RECIPE/'StudioX_System.c').read_bytes())
            copy_file(stage,'README.md',(RECIPE/'README.md').read_bytes())
            jsonfile(stage/'manifest.json',dict(formatVersion=1,id=packid,version=VERSION,displayName=rows[0]['subfamily']+' · CMSIS',vendor=rows[0]['vendor'],devices=devices))
            jsonfile(stage/'provenance.json',dict(formatVersion=1,dfp=record,cmsis={'version':'6.3.0','url':'https://www.keil.com/pack/ARM.CMSIS.6.3.0.pack','filesSha256':{str(p.relative_to(stage)).replace('\\','/'):sha(p.read_bytes()) for p in (stage/'sdk/core').rglob('*') if p.is_file()}},devices=evidence,hardwareValidated=False,downloadEnabled=False))
            hashes={str(p.relative_to(stage)).replace('\\','/'):sha(p.read_bytes()) for p in sorted(stage.rglob('*')) if p.is_file()}
            jsonfile(stage/'files.sha256.json',hashes)
            target=a.output/'packages'/(packid+'-'+VERSION+'.mcupack');target.parent.mkdir(exist_ok=True)
            if target.exists():raise ValueError('Output already exists')
            with zipfile.ZipFile(target,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
                for path in sorted(stage.rglob('*')):
                    if path.is_file():
                        info=zipfile.ZipInfo(path.relative_to(stage).as_posix(),(2000,1,1,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;archive.writestr(info,path.read_bytes())
            index.append(dict(id=packid,version=VERSION,file=target.name,devices=len(rows),bytes=target.stat().st_size,sha256=sha(target.read_bytes())))
            print(packid,len(rows),'devices',target.stat().st_size,'bytes',flush=True)
        jsonfile(a.output/'index.json',index);jsonfile(a.output/'rejected.json',rejected)
    print('RESULT',len(index),'packs',sum(x['devices'] for x in index),'devices',len(rejected),'excluded',flush=True)

if __name__=='__main__':main()
