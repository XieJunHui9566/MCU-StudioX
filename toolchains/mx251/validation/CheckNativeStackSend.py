"""Correctness boundaries, not performance benchmarks. Preserve compiler diagnostics."""
from pathlib import Path
import hashlib,json,os,subprocess
import argparse
ap=argparse.ArgumentParser(description=__doc__)
for name in ('parent-compiler','compiler','toolchain','output'):
 ap.add_argument('--'+name,type=Path,required=True)
args=ap.parse_args()
T=args.toolchain.resolve()
COMPILERS={'parent':args.parent_compiler.resolve(),'candidate':args.compiler.resolve()}
PREFIX='''typedef unsigned char u8; typedef unsigned int u16; typedef unsigned long u32;
#define ABI __reentrant __sdcccall(4)
extern u32 f32(u32,u32) ABI;
extern u16 f16(u16,u16) ABI;
extern u8 f8(u8,u8) ABI;
extern float ff(float,float) ABI;
extern u32 f3(u32,u32,u32) ABI;
extern u32 old(u32,u32) __reentrant;
extern void escape(u32*,u32*) __reentrant;
extern u32 effect(void) __reentrant;
typedef u32(*Fn)(u32,u32) ABI;
'''
# A changing return value is not the expected oracle here: this checks the
# shape and rejected transformations. Execution is checked separately on MCU.
CASES={
 'stack32':('u32 probe(u8 tag,u32 a,u32 b) __reentrant{return f32(a,b);}',False),
 'stack16':('u16 probe(u8 tag,u16 a,u16 b) __reentrant{return f16(a,b);}',False),
 'stack8':('u8 probe(u8 tag,u8 a,u8 b) __reentrant{return f8(a,b);}',False),
 'stack-float':('float probe(u8 tag,float a,float b) __reentrant{return ff(a,b);}',False),
 'third-stack':('u32 probe(u8 tag,u32 a,u32 b,u32 c) __reentrant{return f3(a,b,c);}',False),
 'repeated':('u32 probe(u8 tag,u32 a) __reentrant{return f32(a,a);}',False),
 'mixed-literal':('u32 probe(u8 tag,u32 a) __reentrant{return f32(a,0x12345678UL);}',False),
 'volatile':('u32 probe(u8 tag,volatile u32 a,volatile u32 b) __reentrant{return f32(a,b);}',True),
 'addressed':('u32 probe(u8 tag,u32 a,u32 b) __reentrant{escape(&a,&b);return f32(a,b);}',True),
 'indirect':('u32 probe(Fn fp,u32 a,u32 b) __reentrant{return fp(a,b);}',True),
 'ordinary':('u32 probe(u8 tag,u32 a,u32 b) __reentrant{return old(a,b);}',True),
 'postincrement':('u32 probe(u8 tag,u32 a,u32 b) __reentrant{u32 x=f32(a++,b);return x+a;}',False),
 'intervening-call':('u32 probe(u8 tag,u32 a,u32 b) __reentrant{u32 x=a; b=effect();return f32(x,b);}',False),
 'branch':('u32 probe(u8 tag,u32 a,u32 b) __reentrant{if(tag)a+=b;else b+=a;return f32(a,b);}',False),
 'mixed32':('u32 probe(u32 a,u32 b) __reentrant{return f32(a,b);}',True),
 'mixed16':('u16 probe(u16 a,u16 b) __reentrant{return f16(a,b);}',False),
 'mixed8':('u8 probe(u8 a,u8 b) __reentrant{return f8(a,b);}',False),
 'atomic':('u32 probe(u8 tag,_Atomic(u32) a,_Atomic(u32) b) __reentrant{return f32(a,b);}',True),
}
def clean(p):
 return '\n'.join(s.split(';')[0].strip() for s in p.read_text().splitlines() if s.split(';')[0].strip())
out=args.output.resolve();out.mkdir(parents=True,exist_ok=False)
env=os.environ.copy();env['SDCC_HOME']=str(T);env['PATH']=str(T/'bin')+os.pathsep+env['PATH']
rows=[]
for model in ['small','large']:
 for name,(body,identical) in CASES.items():
  rec=dict(case=name,model=model,requireUnchanged=identical,compilers={})
  for which,exe in COMPILERS.items():
   d=out/(model+'-'+name)/which;d.mkdir(parents=True);(d/'probe.c').write_text(PREFIX+body+'\n',encoding='utf8')
   cmd=[str(exe),'-mmcs251','--model-'+model,'--stack-auto','--mcs251-native-abi','--std-sdcc11','-c','probe.c']
   r=subprocess.run(cmd,cwd=d,env=env,capture_output=True,timeout=30)
   (d/'stdout.log').write_bytes(r.stdout);(d/'stderr.log').write_bytes(r.stderr)
   rec['compilers'][which]=dict(exitCode=r.returncode,command=cmd,asm=clean(d/'probe.asm') if (d/'probe.asm').exists() else None)
   assert not any(x in r.stderr for x in [b'Assertion',b'Internal Error',b'SIGSEGV'])
   if name=='atomic':assert r.returncode==1 and b'error 277: atomics are not supported' in r.stderr and not (d/'probe.rel').exists()
  a,b=[rec['compilers'][n] for n in ['parent','candidate']]
  rec['sameAssembly']=a['asm']==b['asm']
  positive=name in ('stack32','stack16','stack8','stack-float','third-stack')
  rec['passed']=a['exitCode']==b['exitCode'] and (name=='atomic' or a['exitCode']==0) and (not identical or rec['sameAssembly']) and (not positive or not rec['sameAssembly'])
  for x in rec['compilers'].values():x.pop('asm')
  rows.append(rec)
  (out/'results.json').write_text(json.dumps(dict(cases=rows,compilers={k:hashlib.sha256(v.read_bytes()).hexdigest() for k,v in COMPILERS.items()}),indent=2),encoding='utf8')
  print(model,name,'PASS' if rec['passed'] else 'FAIL','same' if rec['sameAssembly'] else 'changed',flush=True)
  assert rec['passed'],name
