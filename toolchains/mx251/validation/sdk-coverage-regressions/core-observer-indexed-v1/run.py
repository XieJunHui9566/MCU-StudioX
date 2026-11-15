"""Explicit current-tool execution, plus immutable historical wrong-code replay."""
from pathlib import Path
import argparse,hashlib,json,os,re,sys
from bounded import run
from raw_model import execute

ROOT=Path(__file__).resolve().parent
def load(p):return json.loads(p.read_text('utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def pin(p):return dict(path=str(p.resolve()),sha256=sha(p))
def save(p,x):p.write_text(json.dumps(x,ensure_ascii=False,indent=2)+'\n','utf8')

def instructions(lst,rel):
 """No linker guess: reject all relocation entries, unresolved refs or data."""
 lines=rel.read_text().splitlines();assert lines[0]=='XH3','Unsupported REL encoding'
 areas=[];pending=None;memory={}
 for line in lines[1:]:
  if line.startswith('A '):
   m=re.fullmatch(r'A (\S+) size ([0-9A-Fa-f]+) flags ([0-9A-Fa-f]+) addr ([0-9A-Fa-f]+)',line);assert m,line
   areas.append(dict(name=m[1],size=int(m[2],16),flags=int(m[3],16),address=int(m[4],16)))
  elif line.startswith('S '):assert ' Ref'not in line,'Unresolved symbol: '+line
  elif line.startswith('T '):assert pending is None;pending=bytes.fromhex(line[2:]);assert len(pending)>=3
  elif line.startswith('R '):
   raw=bytes.fromhex(line[2:]);assert pending is not None and len(raw)==4 and raw[:2]==b'\0\0','Relocation not qualified'
   area=areas[int.from_bytes(raw[2:],'big')];payload=pending[3:];start=int.from_bytes(pending[:3],'big')
   if payload:
    assert area['name']=='CSEG'and area['flags']==0x20 and area['address']==0,'Unexpected payload area'
    for i,value in enumerate(payload):assert start+i not in memory;memory[start+i]=value
   pending=None
  else:assert not line or line[0]in 'HMO',line
 assert pending is None
 codeAreas=[a for a in areas if a['flags']&0x20 and a['size']]
 assert len(codeAreas)==1 and codeAreas[0]['name']=='CSEG'
 assert set(memory)==set(range(codeAreas[0]['size'])),'Sparse/extra machine bytes'
 rows=[];seen={}
 for line in lst.read_text().splitlines():
  m=re.match(r'\s*([0-9A-Fa-f]{6})\s+((?:[0-9A-Fa-f]{2}\s+)+)\[\s*\d+\]\s+(\d+)\s',line)
  if not m:continue
  pc=int(m[1],16);raw=bytes.fromhex(m[2]);rows.append(dict(pc=pc,bytes=raw.hex()))
  for i,value in enumerate(raw):assert pc+i not in seen;seen[pc+i]=value
 assert rows and seen==memory,'LST versus actual REL bytes differ'
 return rows

def main():
 p=argparse.ArgumentParser(description=__doc__)
 p.add_argument('--compiler',type=Path,required=True);p.add_argument('--assembler',type=Path,required=True)
 p.add_argument('--toolchain',type=Path,required=True);p.add_argument('--source-lock',type=Path,required=True)
 p.add_argument('--output',type=Path,required=True);p.add_argument('--timeout',type=int,default=30)
 a=p.parse_args();assert 1<=a.timeout<=120
 a.compiler=a.compiler.resolve();a.assembler=a.assembler.resolve();a.toolchain=a.toolchain.resolve();a.source_lock=a.source_lock.resolve();a.output=a.output.resolve()
 for item in load(ROOT/'manifest.json')['files']:
  path=ROOT/item['path'];assert path.is_file()and sha(path)==item['sha256'],path
 sourceLock=load(a.source_lock);assert isinstance(sourceLock,list)and sourceLock
 for item in sourceLock:assert sha(Path(item['path']))==item['sha256'],item['path']
 for path in(a.compiler,a.assembler):assert path.is_file(),path
 assert (a.toolchain/'bin').is_dir()
 a.output.mkdir(parents=True,exist_ok=False)
 provenance=dict(compiler=pin(a.compiler),assembler=pin(a.assembler),sourceLock=pin(a.source_lock),runner=pin(Path(__file__)),fixtureManifest=pin(ROOT/'manifest.json'))
 save(a.output/'invocation.json',dict(argv=sys.argv,tools=provenance,timeoutPerChild=a.timeout))
 inputs=load(ROOT/'cases/inputs.json');historical=load(ROOT/'historical-v5/results.json');history=[];baseline={}
 for row in historical['rows']:
  states=[]
  for role in('off','on'):
   d=ROOT/'historical-v5'/(row['model']+'-'+row['observer']+'-'+role)
   program=instructions(d/'probe.lst',d/'probe.rel')
   assert program==[dict(pc=x['pc'],bytes=x['bytes'])for x in row[role]['instructions']]
   state=execute(program);assert all(state[k]==row[role][k]for k in state)
   assert state['events']==inputs['expectedEvents'];states.append(state)
   if role=='off':baseline[(row['model'],row['observer'])]=program
  assert states[0]['result']!=states[1]['result'],'Historical V5 error disappeared'
  assert states[0]['events']==states[1]['events']
  history.append(dict(model=row['model'],observer=row['observer'],caught=True,off=states[0],on=states[1]))
 assert len(history)==4
 save(a.output/'historical-replay.json',dict(status='PASS_FOUR_HISTORICAL_WRONG_RESULTS_CAUGHT',rows=history))
 env={k:v for k,v in os.environ.items()if not k.upper().startswith('MX251_')and k.upper()not in('SDCC_HOME','SDCC_INCLUDE','SDCC_LIB','SDCC_BIN','CPATH','C_INCLUDE_PATH')}
 env['SDCC_HOME']=str(a.toolchain);env['PATH']=str(a.toolchain/'bin')+os.pathsep+env.get('PATH','')
 common=['-mmcs251','--opt-code-speed','--stack-auto','--mcs251-native-abi','--mcs251-wide-backend','--mcs251-native-indexed-field-load','--mcs251-indexed-field-capability','source24-byte-v1','--mcs251-indexed-field-contract','objects.contract','-S','probe.c','-o','probe.asm']
 on=['--mcs251-native-indexed-two-use','--mcs251-native-indexed-two-use-word','--mcs251-indexed-word-capability','source24-word-v1']
 children=[];current=[];negative=[]
 def child(argv,d,label,expected=0):
  q=run(argv,d,env,d/(label+'.stdout.log'),d/(label+'.stderr.log'),d/(label+'.child.json'),timeout=a.timeout)
  children.append(q);assert q['status']=='EXITED'and q.get('activeChildren')==0,q
  assert(q['exitCode']==0)if expected==0 else(q['exitCode']!=0),q
  return q
 def source_case(d,observer,renamed):
  text=(ROOT/'cases'/(observer+'.c')).read_text();contract=(ROOT/'cases/objects.contract').read_text()
  if renamed:
   # Mechanical changes affect identifiers and the actual typed contract;
   # no pass criterion consults any of these identifiers.
   changes={'f':'sample_view','cfg':'record_view','value':'payload','tag':'sentinel','observer':'status_view','io':'port_view'}
   for old,new in changes.items():
    text=re.sub(r'\b'+old+r'\b',new,text)
    contract=contract.replace('___m251r4_'+old+'\t','___m251r4_'+new+'\t')
    contract=re.sub(r'\b'+old+r'(?=\()',new,contract)
  (d/'probe.c').write_text(text,'ascii');(d/'objects.contract').write_text(contract,'ascii')
  return[pin(d/'probe.c'),pin(d/'objects.contract')]
 for model in('small','large'):
  for observer in('acc','b','psw','psw1'):
   for renamed in(False,True):
    sides=[];group=model+'-'+observer+('-renamed'if renamed else'-original')
    for role in('off','on'):
     d=a.output/(group+'-'+role);d.mkdir();pins=source_case(d,observer,renamed)
     argv=[a.compiler,'--model-'+model]+common
     if role=='on':argv+=on+['--mcs251-indexed-nonobserver-sfr','b1:ff']
     compileResult=child(argv,d,'compile');assembleResult=child([a.assembler,'-plosgff','probe.rel','probe.asm'],d,'assemble')
     program=instructions(d/'probe.lst',d/'probe.rel')
     sides.append(dict(role=role,program=program,source=pins,compile=compileResult,assemble=assembleResult,artifacts=[pin(d/x)for x in('probe.asm','probe.rel','probe.lst')]))
    equal=sides[0]['program']==sides[1]['program']
    if observer in('acc','b'):
     states=[execute(x['program'])for x in sides]
     assert states[0]==states[1],'Current core observer differs: '+group
     assert all(x['events']==inputs['expectedEvents']for x in states)
     assert sides[0]['program']==baseline[(model,observer)],'Default-OFF baseline encoding changed; requires explicit review'
    else:
     states=None;assert equal,'PSW/PSW1 requires exact encoded conservative fallback'
    current.append(dict(model=model,observer=observer,renamed=renamed,actualEncodedEqual=equal,states=states,sides=sides))
    save(a.output/'current-progress.json',dict(status='RUNNING',rows=current));print(group,'PASS',flush=True)
  invalid=['e0:ff','d0:ff','d1:ff','f0:ff','81:ff','82:ff','e3:ff','a8:ff','b1:ff,b1:01','b1:00','b1:ff,','b1:GG']
  for number,cap in enumerate(invalid):
   d=a.output/(model+'-invalid-'+str(number));d.mkdir();pins=source_case(d,'acc',False)
   q=child([a.compiler,'--model-'+model]+common+on+['--mcs251-indexed-nonobserver-sfr',cap],d,'compile',expected=1)
   negative.append(dict(model=model,cap=cap,rejected=True,source=pins,child=q))
 assert len(current)==16 and len(negative)==24 and len(children)==88
 for item in sourceLock:assert sha(Path(item['path']))==item['sha256'],item['path']
 assert sha(a.compiler)==provenance['compiler']['sha256']and sha(a.assembler)==provenance['assembler']['sha256']
 save(a.output/'results.json',dict(status='PASS_CURRENT_MACHINE_CORE_OBSERVERS_AND_HISTORICAL_REJECTION',tools=provenance,current=current,invalidCapabilities=negative,historical=history,children=children,allChildrenExited=True,hardware=False,scope='Two compiler memory models, original and renamed leaf fixtures; no TargetProfile/peripheral/ISR/hardware or common-suite registration claim.',limitations=['Default OFF has a frozen byte fence for the historical ACC/B case; an intentional unrelated emitter change needs explicit independent review, not silent golden replacement.','Exact PSW/PSW1 bytes are the conservative proof; this model does not implement arbitrary PSW1 semantics.','Synthetic complete-object contract is grammar/admission evidence; full linked objects and callers remain separate.']))
 print('PASS 16 current pairs, 24 invalid capabilities, 4 historical errors; 88 bounded children EXITED',flush=True)

if __name__=='__main__':main()
