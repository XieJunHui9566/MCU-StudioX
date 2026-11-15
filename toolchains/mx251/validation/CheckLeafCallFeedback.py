"""Offline boundaries for explicit leaf contracts and regenerated call saves."""
from pathlib import Path
import argparse,hashlib,json,os,re,subprocess
p=argparse.ArgumentParser(description=__doc__)
for n in ('compiler','toolchain','output'):p.add_argument('--'+n,type=Path,required=True)
a=p.parse_args();out=a.output.resolve();out.mkdir(parents=True,exist_ok=False);compiler=a.compiler.resolve();tools=a.toolchain.resolve()
env=os.environ.copy();env['SDCC_HOME']=str(tools);env['PATH']=str(tools/'bin')+os.pathsep+env['PATH']
prefix='typedef unsigned char u8;typedef unsigned int u16;typedef unsigned long u32;\n#define ABI __reentrant __sdcccall(4)\n'
cases={
 'ordinary-stack':'u32 leaf(u32 a,u32 b) __reentrant{return a^b;} u32 caller(u32 a,u32 b,u32 c) __reentrant{u32 t=a+b;u32 r=leaf(a,b);return r+t+a+b+c;}',
 'native-stack':'u32 leaf(u32 a,u32 b) ABI{return a^b;} u32 caller(u32 a,u32 b,u32 c) ABI{u32 t=a+b;u32 r=leaf(a,b);return r+t+a+b+c;}',
 'mixed-width':'u8 leaf(u8 a,u16 b) ABI{return a+(u8)b;} u32 caller(u32 a,u16 b,u8 c) __reentrant{u8 r=leaf(c,b);return a+b+c+r;}',
 'local-object':'u32 leaf(u32 a,u32 b) ABI{return a^b;} extern void escape(u32 *) __reentrant;u32 caller(u32 a,u32 b) __reentrant{u32 c=a+b;escape(&c);return leaf(a,b)+c+a;}',
 'static-shadow':'static u32 leaf(u32 a,u32 b) ABI{return a^b;} u32 caller(u32 a,u32 b) ABI{u32 r=leaf(a,b);return r+a+b;}',
 'unknown-callee':'extern u32 leaf(u32,u32) ABI;u32 caller(u32 a,u32 b) ABI{u32 r=leaf(a,b);return r+a+b;}',
 'recursive':'u32 leaf(u32 a,u32 b) ABI{if(b)return leaf(a,b-1)+b;return a;} u32 caller(u32 a,u32 b) ABI{return leaf(a,b)+a+b;}',
 'indirect':'u32 leaf(u32 a,u32 b) ABI{return a^b;} typedef u32(*Fn)(u32,u32) ABI;Fn volatile fp=leaf;u32 caller(u32 a,u32 b) ABI{return fp(a,b)+a+b;}',
}
records=[]
def run(d,body,feedback=None):
 d.mkdir(parents=True);(d/'probe.c').write_text(prefix+body,encoding='utf8')
 cmd=[str(compiler),'-mmcs251','--model-'+model,'--stack-auto','--mcs251-native-abi','--mcs251-wide-ordinary','-c','probe.c','-o','probe.rel']
 if feedback is not None:(d/'effects.txt').write_bytes(feedback);cmd+=['--mcs251-leaf-effects','effects.txt']
 r=subprocess.run(cmd,cwd=d,env=env,capture_output=True,timeout=60)
 (d/'stdout.log').write_bytes(r.stdout);(d/'stderr.log').write_bytes(r.stderr)
 (d/'command.json').write_text(json.dumps(cmd,indent=2),encoding='utf8')
 return r,(d/'probe.asm').read_text('utf8') if (d/'probe.asm').exists() else ''
def code(text):return [s for l in text.splitlines() if (s:=l.split(';')[0].strip())]
def feedback(text,mask=None,mismatch=False):
 lines=['MX251-LEAF-EFFECTS-1']
 for m in re.finditer(r'^; mx251-effects (.*)$',text,re.M):
  f=dict(x.split('=',1) for x in m[1].split())
  if f['function'] not in ('_leaf','___m251r4_leaf') or f['complete']!='1' or f['calls']!='0' or int(f['memory'])&2:continue
  lines.append(f['function']+' '+(str(6-int(f['abi'])) if mismatch else f['abi'])+' '+(mask or f['writes']))
 return ('\n'.join(lines)+'\n').encode()
for model in ('small','large'):
 for name,body in cases.items():
  d=out/(model+'-'+name);r,original=run(d/'baseline',body);assert r.returncode==0,r.stderr
  contract=feedback(original)
  if name=='static-shadow':contract=b'MX251-LEAF-EFFECTS-1\n___m251r4_leaf 4 00000000\n'
  r,final=run(d/'feedback',body,contract);assert r.returncode==0,r.stderr
  rows=re.findall(r'^; mx251-call-save (.*)$',final,re.M)
  assert all('verified=1' in x for x in rows)
  if name in ('static-shadow','unknown-callee','recursive','indirect'):assert not rows and code(original)==code(final),name
  if name in ('ordinary-stack','native-stack'):assert rows,(name,'missing positive save elimination')
  for variant,data in [('top',feedback(original,'ffffffff')),('abi-mismatch',feedback(original,mismatch=True))]:
   r,kept=run(d/variant,body,data);assert r.returncode==0 and code(original)==code(kept),(name,variant)
  records.append(dict(case=model+'-'+name,passed=True,omissions=rows))
model='small';body=cases['ordinary-stack']
bad={'header':b'NO\n','abi':b'MX251-LEAF-EFFECTS-1\n_leaf 7 00000000\n','mask':b'MX251-LEAF-EFFECTS-1\n_leaf 2 fffffffff\n','duplicate':b'MX251-LEAF-EFFECTS-1\n_leaf 2 00000000\n_leaf 2 00000000\n','nul':b'MX251-LEAF-EFFECTS-1\x00\n','long':b'MX251-LEAF-EFFECTS-1\n'+b'x'*5000+b'\n'}
for name,data in bad.items():
 d=out/('reject-'+name);r,_=run(d,body,data);assert r.returncode!=0 and b'invalid feedback contract' in r.stderr and not (d/'probe.rel').exists(),name
 records.append(dict(case='reject-'+name,passed=True))
(out/'results.json').write_text(json.dumps(dict(passed=True,compilerSha256=hashlib.sha256(compiler.read_bytes()).hexdigest(),cases=records),indent=2)+'\n',encoding='utf8')
print('leaf call feedback boundary groups passed:',len(records))
