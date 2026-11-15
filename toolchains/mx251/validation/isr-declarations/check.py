from pathlib import Path
import argparse, hashlib, json, os, shutil, subprocess
ap=argparse.ArgumentParser(description="Offline MX251 ISR redeclaration regression; no hardware access.")
ap.add_argument('--compiler',type=Path,required=True)
ap.add_argument('--parent-compiler',type=Path,required=True)
ap.add_argument('--toolchain',type=Path,required=True)
ap.add_argument('--startup',type=Path,required=True)
ap.add_argument('--output',type=Path,required=True)
a=ap.parse_args()
C=a.compiler.resolve(); parent=a.parent_compiler.resolve(); T=a.toolchain.resolve(); startup=a.startup.resolve()
root=a.output.resolve(); root.mkdir(parents=True,exist_ok=False); results=[]
env=os.environ.copy(); env['SDCC_HOME']=str(T); env['PATH']=str(T/'bin')+';'+str(Path(os.environ['SystemRoot'])/'System32')
for k in ('SDCC_INCLUDE','SDCC_LIB','SDCC_BIN','SDCCLIB','SDCCDIR','CPATH','C_INCLUDE_PATH'): env.pop(k,None)
decl='void tick(void) __interrupt(1);\n'
body='void tick(void) __interrupt(1) { ++state; }\n'
prefix='volatile unsigned char state;\n'
main='void main(void) { for(;;){} }\n'
positive=[
 ('after-body',body+decl+main,body+main),
 ('before-body',decl+decl+body+main,decl+body+main),
 ('both-sides',decl+decl+body+decl+decl+main,decl+body+main),
 ('after-main',body+main+decl,body+main),
 ('extern',body+'extern '+decl+main,body+main),
 ('redundant-reentrant-stack-auto',body+'void tick(void) __interrupt(1) __reentrant;\n'+main,body+main),
 ('static-extern','static '+body+'extern '+decl+main,'static '+body+main),
 ('static-repeat','static '+body+'static '+decl+main,'static '+body+main),
 ('macro-header','#include "irq.h"\n'+body+'#include "irq.h"\n'+main,decl+body+main),
 ('decl-chain-head',body+'void tick(void) __interrupt(1), helper(void);\nvoid helper(void) {}\n'+main,body+'void helper(void);\nvoid helper(void) {}\n'+main),
 ('decl-chain-tail',body+'void helper(void), tick(void) __interrupt(1);\nvoid helper(void) {}\n'+main,body+'void helper(void);\nvoid helper(void) {}\n'+main),
]
negative=[
 ('changed-slot-after',body+'void tick(void) __interrupt(2);\n'+main),
 ('changed-slot-before',decl+'void tick(void) __interrupt(2);\n'+main),
 ('definition-slot-conflict',decl+'void tick(void) __interrupt(2) {}\n'+main),
 ('duplicate-body',body+decl+body+main),
 ('two-names-one-slot',body+'void other(void) __interrupt(1) {}\n'+main),
 ('ordinary-prototype',body+'void tick(void);\n'+main),
 ('changed-return',body+'int tick(void) __interrupt(1);\n'+main),
 ('changed-parameters',body+'void tick(int x) __interrupt(1);\n'+main),
 ('changed-bank',body+'void tick(void) __interrupt(1) __using(1);\n'+main),
 ('changed-naked',body+'void tick(void) __interrupt(1) __naked;\n'+main),
 ('changed-critical',body+'void tick(void) __interrupt(1) __critical;\n'+main),
 ('static-after-external',body+'static '+decl+main),
]
commands=[]
def run(d,label,cmd):
    cmd=list(map(str,cmd)); q=subprocess.run(cmd,cwd=d,env=env,capture_output=True,timeout=60)
    (d/(label+'.stdout.log')).write_bytes(q.stdout); (d/(label+'.stderr.log')).write_bytes(q.stderr)
    commands.append(dict(case=d.relative_to(root).as_posix(),label=label,command=cmd,exitCode=q.returncode))
    (root/'commands.json').write_text(json.dumps(commands,indent=2)); return q
def compile(d,src,flags,exe=C):
    d.mkdir(parents=True); (d/'test.c').write_text(prefix+src); (d/'irq.h').write_text(decl)
    return run(d,'compile',[exe,*flags,'-c','test.c'])
def link(d,flags):
    shutil.copyfile(startup,d/'startup.asm')
    q=run(d,'startup',[T/'bin/sdas251.exe','-plosgff','startup.rel','startup.asm']); assert not q.returncode,q.stderr
    q=run(d,'link',[C,*flags,'--code-loc','0xff0000','--code-size','0x10000','--xram-loc','0x10000','--xram-size','0x2000','--iram-size','0x100','--mcs251-stack','0x400,0x400,0x1000','test.rel','startup.rel','-o','firmware.hex']); assert not q.returncode,q.stderr
    return (d/'firmware.hex').read_bytes()
for model in ('small','large'):
    for vectors in (False,True):
        flags=['-mmcs251','--model-'+model,'--stack-auto']+(['--mcs251-vector-count','62'] if vectors else [])
        tag=model+('-vectors' if vectors else '-default')
        for name,src,normalized in positive:
            d=root/(tag+'-'+name); q=compile(d,src,flags); assert q.returncode==0,(d,q.stderr)
            ref=d/'normalized'; q=compile(ref,normalized,flags); assert not q.returncode,(ref,q.stderr)
            assert link(d,flags)==link(ref,flags),d
            asm=(d/'test.asm').read_text(); assert asm.count('_tick:\n')==1,d
            if vectors: assert asm.count('__mx251_irq_def_v1_1__tick = _tick')==1,d
            results.append(dict(case=d.name,passed=True,normalizedFirmwareIdentical=True,singleISRBody=True))
        for name,src in negative:
            d=root/(tag+'-'+name); q=compile(d,src,flags); assert q.returncode!=0,('invalid accepted',d)
            assert b'error ' in q.stderr,(d,q.stderr)
            results.append(dict(case=d.name,passed=True,invalidDeclarationRejected=True))
        print(tag,'passed',flush=True)
for model in ('small','large'):
    d=root/(model+'-reentrant-without-stack-auto')
    q=compile(d,body+'void tick(void) __interrupt(1) __reentrant;\n'+main,['-mmcs251','--model-'+model])
    assert q.returncode!=0,('changed reentrant ABI accepted without stack-auto',d)
    results.append(dict(case=d.name,passed=True,invalidDeclarationRejected=True))
# Shared frontend file, but change deliberately limited to MCS251 ISR declarations.
for name,src in [('ordinary','unsigned long f(unsigned long x){ return x+3; }\nunsigned long f(unsigned long x);\n'+main),('isr',body+decl+main)]:
    for target in ('mcs51','mcs251'):
        if target=='mcs251' and name=='isr': continue
        outputs=[]
        for label,exe in [('parent',parent),('candidate',C)]:
            d=root/(target+'-'+name+'-'+label); q=compile(d,src,['-m'+target,'--model-small','--stack-auto'],exe)
            outputs.append((q.returncode,(d/'test.rel').read_bytes() if q.returncode==0 else q.stderr))
        assert outputs[0]==outputs[1],(target,name)
        results.append(dict(case=target+'-'+name,passed=True,parentBehaviorUnchanged=True))
(root/'results.json').write_text(json.dumps(results,indent=2)); print('passed',len(results))
