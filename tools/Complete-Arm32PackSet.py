"""Assemble only packs whose complete model matrix passed against the same imported payload."""
import argparse, csv, hashlib, json, shutil, zipfile
from collections import Counter
from pathlib import Path

def sha(data):return hashlib.sha256(data).hexdigest()
def read(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def save(path,data):path.parent.mkdir(parents=True,exist_ok=True);path.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

def inspect(path):
    with zipfile.ZipFile(path) as z:
        index=json.loads(z.read('files.sha256.json'))
        names=set(z.namelist())
        if len(names)!=len(z.infolist()) or names!=set(index)|{'files.sha256.json'}:raise ValueError('Unexpected archive entries: '+str(path))
        for name,expected in index.items():
            if sha(z.read(name)).lower()!=expected.lower():raise ValueError('Hash mismatch: '+str(path)+'/'+name)
        manifest=json.loads(z.read('manifest.json'))
        if manifest['formatVersion']!=1 or not all(d['architecture']=='arm' for d in manifest['devices']):raise ValueError('Not an ARM format-1 pack: '+str(path))
        return manifest,index

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--candidate',action='append',nargs=2,metavar=('PACK_ROOT','VALIDATION_ROOT'),required=True)
    p.add_argument('--existing',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p.add_argument('--omit-pack',action='append',nargs=2,default=[],metavar=('PACK_ID','REASON'))
    a=p.parse_args();root=a.output
    if root.exists():raise ValueError('Use a new delivery directory')
    selected={};excluded=[]
    for directory,validation in a.candidate:
        directory=Path(directory);validation=Path(validation)
        if (directory/'rejected.json').exists():excluded+=read(directory/'rejected.json')
        for entry in read(directory/'index.json'):selected[entry['id']]=(directory/'packages'/entry['file'],validation)
    for packid,reason in a.omit_pack:
        if packid not in selected:raise ValueError('Unknown omitted pack: '+packid)
        del selected[packid];excluded.append(dict(pack=packid,device='*',reason=reason))
    verified=[];matrix=[]
    for packid,(path,validation) in sorted(selected.items()):
        manifest,index=inspect(path)
        expected={packid+'/'+d['id']+'/'+t['id'] for d in manifest['devices'] for t in d['templates']}
        matches=[]
        for f in validation.glob('batch-*/matrix.json'):
            installed=f.parent/'repository'/packid/manifest['version']/'files.sha256.json'
            if not installed.exists():continue
            installed_index=read(installed)
            if {k:v.lower() for k,v in installed_index.items()}!={k:v.lower() for k,v in index.items()}:raise ValueError('Validation payload differs: '+packid)
            matches += [x for x in read(f) if x['model'].startswith(packid+'/')]
        if {x['model'] for x in matches}!=expected or len(matches)!=len(expected):raise ValueError('Incomplete/duplicate model matrix: '+packid)
        if any(x['status']!='PASS' for x in matches) or not any(x['compiled'] for x in matches):raise ValueError('Failed/uncompiled pack: '+packid)
        matrix+=matches;verified.append((path,manifest,'new',sum(x['compiled'] for x in matches)))
    for entry in read(a.existing):
        path=Path(entry['path']);manifest,_=inspect(path)
        if manifest['id'] in selected:raise ValueError('Existing/new pack ID collision: '+manifest['id'])
        verified.append((path,manifest,'existing',0))
    # 所有验证通过后才创建交付目录；半成品留在独立 candidate 目录。
    root.mkdir(parents=True);catalog=[];models=[]
    for path,m,kind,builds in sorted(verified,key=lambda x:x[1]['id']):
        folder=root/'packages'/m['id'].split('.')[0];folder.mkdir(parents=True,exist_ok=True)
        target=folder/path.name;shutil.copyfile(path,target)
        catalog.append(dict(id=m['id'],version=m['version'],vendor=m['vendor'],displayName=m['displayName'],file=target.relative_to(root).as_posix(),devices=len(m['devices']),sha256=sha(target.read_bytes()),bytes=target.stat().st_size,origin=kind,compiledModels=builds,verification='all-model-project-and-representative-build' if kind=='new' else 'existing-package-sha256-verification'))
        for d in m['devices']:
            models.append(dict(pack=m['id'],device=d['id'],displayName=d['displayName'],flashBytes=d['flashBytes'],ramBytes=d['ramBytes'],templates=';'.join(t['id'] for t in d['templates']),downloadDeclared=bool(d.get('openOcd')),origin=kind))
    accepted_names={name for row in models for name in (row['device'],row['displayName'])}
    exclusions={}
    for row in excluded:
        if row.get('device','').lstrip('-') in accepted_names:continue
        exclusions[(row.get('source',row.get('pack','')),row['device'])]=row
    excluded=list(exclusions.values())
    save(root/'catalog.json',catalog);save(root/'validation/new-model-matrix.json',matrix);save(root/'validation/excluded.json',excluded)
    for name,rows in [('packages.csv',catalog),('devices.csv',models)]:
        with (root/name).open('w',encoding='utf-8-sig',newline='') as f:
            writer=csv.DictWriter(f,fieldnames=list(rows[0]));writer.writeheader();writer.writerows(rows)
    source=Path(__file__).resolve().parents[1]/'examples/packs/arm32-expansion/sources.lock.json';shutil.copyfile(source,root/'sources.lock.json')
    counts=Counter();newcounts=Counter()
    for _,m,kind,_ in verified:
        vendor=m['vendor'].split(' / ')[0]
        counts[vendor]+=len(m['devices'])
        if kind=='new':newcounts[vendor]+=len(m['devices'])
    summary=dict(packs=len(catalog),devices=len(models),newPacks=sum(c['origin']=='new' for c in catalog),newDevices=sum(c['devices'] for c in catalog if c['origin']=='new'),newBuilds=sum(c['compiledModels'] for c in catalog),existingPacks=sum(c['origin']=='existing' for c in catalog),excludedEntries=len(excluded),byVendor=counts,newByVendor=newcounts,hardwareAccessed=False)
    save(root/'summary.json',summary)
    table='\n'.join('| '+v+' | '+str(newcounts[v])+' | '+str(n)+' |' for v,n in sorted(counts.items()))
    text=f'''# ARM32 MCU Pack 集合（2026-09-30）

共 **{summary['packs']} 个独立包、{summary['devices']} 个器件条目**。本轮新增 **{summary['newPacks']} 包、{summary['newDevices']} 个条目**；另收录 {summary['existingPacks']} 个现有 ARM 包。器件条目沿用原厂 DFP 命名，有些包含封装通配符，不等同于独立芯片设计数量。

| 厂商 | 本轮新增条目 | 集合总条目 |
| --- | ---: | ---: |
{table}

## 使用

在 MCU StudioX 的器件包管理中导入 `packages/` 下所需子系列的 `.mcupack`，然后选择准确型号和模板。集合目录或外层 ZIP 不是单个芯片包；不要直接导入外层 ZIP。无需修改系统 PATH 或安装全局 Python/Node。完整型号见 `devices.csv`，包名和 SHA-256 见 `packages.csv` / `catalog.json`。

新增包提供 **C / CMSIS 基础工程**：原厂寄存器头、准确启动向量和复位步骤、型号内存布局、`StudioX_System.h/.c` 时基以及 ELF/BIN/HEX 构建。未附带 HAL/SPL 全功能示例、RTOS、USB/BLE 协议栈或图形引脚数据库；部分用于系统时钟的原厂驱动源码随包编译。已有包保留其原有模板和能力，包括 STM32 的 HAL/SPL/RTOS 和 Raspberry Pi 的 C SDK / MicroPython。

## 验证和边界

新增全部 {summary['newDevices']} 条目已通过真实 IDE 服务的包导入、工程生成与内存配置检查，完成 **{summary['newBuilds']} 次代表性 GCC 编译**，生成 ELF/BIN/HEX 并检查入口、初始栈、Flash 边界；LPC 额外验证启动校验和，Kinetis 验证 Flash 配置区。编译按 CPU、宏、启动源码和模板组合选择容量边界，不声称每个型号都单独编译过。记录见 `validation/new-model-matrix.json`。已有 {summary['existingPacks']} 包本轮重新核对全部归档文件 SHA-256，未重复其历史硬件验收。

**本轮没有连接或操作硬件；新增包的下载/调试入口未开放。** 现有包的能力声明保持原样，不能据此认为本轮再次做过实板验证。

各包采用厂商系统文件的默认时钟，实际板卡应核对外部晶振和时钟配置。少数旧 SDK 不提供动态时钟更新函数，使用厂商 `SystemInit` 配套的时钟变量；修改时钟时需同步调整该变量和 SysTick。毫秒延迟只能在中断开启的线程上下文使用。模板不预设板上 LED/串口引脚。

独立 RAM、CCM、DTCM 不盲目拼接，链接器只使用原厂声明的主 RAM 区，其他区域保留在 `provenance.json`。AT32F403A/F407 使用 DFP 默认 96 KiB RAM；扩容配置不是默认内存。多核、部分不连续 Flash、缺少对应启动/头文件的型号列于 `validation/excluded.json`，未伪装为支持。部分 LPC11xx/LPC17xx DFP 属于原厂旧版资料，保留来源，不宣称其维护状态。

来源锁定见 `sources.lock.json`；各包内含原始 PDSC、启动证据、许可证和文件哈希。厂商代码保留自身许可。此集合只用于本地交付，未安装进当前用户的器件包库。公开发布必须另经 `Prepare-Arm32PublicRelease.py` 筛选许可并保留已有公开修订，不可将本地 ZIP 直接上传。
'''
    (root/'README.md').write_text(text,encoding='utf-8')
    print(json.dumps(summary,ensure_ascii=True),flush=True)

if __name__=='__main__':main()
