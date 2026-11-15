"""Check emitted effects describe final accepted assembly, not discarded trials."""
from pathlib import Path
import argparse,json,re,subprocess,hashlib
p=argparse.ArgumentParser(description=__doc__)
for n in ('builds','reader','output'):p.add_argument('--'+n,type=Path,required=True)
a=p.parse_args();out=a.output.resolve();out.mkdir(parents=True,exist_ok=False)
read=lambda p:json.loads(p.read_text('utf-8-sig'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
results=read(a.builds/'results.json');assert results['status']=='passed'
rows=[]
for c in results['cases']:
    if 'firmwareSha256' not in c:continue
    root=a.builds/c['id']/'project/.build/mx251/latest';data=read(root/'call-effects.json')
    for u in data['units']:
        asm=root/u['artifacts'][0]['path'];text=asm.read_text('utf-8-sig')
        # Accepted markers are emitted once by the selected wide transaction.
        assert len(u['functions'])==len(re.findall(r'^; mcs251 wide-mir accepted:',text,re.M)),str(asm)
        for f in u['functions']:
            start=list(re.finditer('^'+re.escape(f['function'])+':$',text,re.M));assert len(start)==1
            end=text.index('; mx251-effects schema=1 function='+f['function']+' ',start[0].start())
            body=text[start[0].start():end];(out/'current-body.asm').write_text(body,encoding='utf8')
            r=subprocess.run([str(a.reader.resolve()),str(out/'current-body.asm')],capture_output=True,timeout=10);assert r.returncode==0,r.stderr
            actual=json.loads(r.stdout)
            assert all(f[k]==v for k,v in actual.items()),(c['id'],f['function'],f,actual)
            rows.append(dict(case=c['id'],unit=u['unit'],function=f['function'],assemblySha256=sha(asm),effects=actual))
(out/'result.json').write_text(json.dumps(dict(passed=True,readerSha256=sha(a.reader),checkedBodies=len(rows),bodies=rows),indent=2)+'\n',encoding='utf8')
print('final assembly summaries verified:',len(rows))
