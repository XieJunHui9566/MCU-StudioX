"""Audit complete-project effects, without modifying code or predicting cycles.

Metadata is bound to the same build receipt, input snapshot, assembly and objects.
An unaffected save group is a research candidate, not proof of removability.
"""
from pathlib import Path
import argparse, collections, hashlib, json

def read(p): return json.loads(p.read_text('utf-8-sig'))
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def audit(root):
    receipt=read(root/'receipt.json');data=read(root/'call-effects.json')
    if receipt['status']!='complete' or sha(root/'call-effects.json')!=receipt['callEffects']['sha256']:
        raise ValueError('effects/receipt identity mismatch')
    if data['schema']!=1 or data['mode']!='audit-only' or data['optimizationEnabled'] is not False:
        raise ValueError('unsupported audit contract')
    for k in ('compilerSha256','profileLockSha256','projectSnapshotSha256','firmwareSha256','inputs'):
        if data[k]!=receipt[k]:raise ValueError('mixed effects identity: '+k)
    entries=[{'path':'project.snapshot.json','sha256':data['projectSnapshotSha256']},
             {'path':'firmware.hex','sha256':data['firmwareSha256']}]
    entries += [e for u in data['units'] for e in u['artifacts']]
    entries += [dict(path=p,sha256=e['sha256']) for p,e in data['inputs'].items()]
    for e in entries:
        p=(root/e['path']).resolve()
        if not p.is_relative_to(root.resolve()) or sha(p)!=e['sha256']:
            raise ValueError('stale or outside artifact: '+e['path'])
    functions=collections.defaultdict(list)
    for u in data['units']:
        for f in u['functions']:functions[f['function']].append((u['unit'],f))
    rows=[];reasons=collections.Counter()
    for u in data['units']:
        for c in u['calls']:
            row=dict(callerUnit=u['unit'],**c);groups=[int(s,16) for s in c['saved'].split(',') if s]
            row.update(savedGroups=len(groups),savedBytes=sum(x.bit_count() for x in groups))
            matches=functions[c['callee']]
            reason='eligible-for-further-proof'
            if c['complete']!='1':reason='unknown-caller-effects'
            elif len(matches)!=1:reason='missing-or-ambiguous-callee'
            elif matches[0][1]['complete']!='1' or matches[0][1]['calls']!='0':reason='unknown-or-nonleaf-callee'
            elif matches[0][1]['abi']!=c['abi']:reason='abi-mismatch'
            if not groups:reason='no-register-save'
            row['classification']=reason;reasons[reason]+=1
            row.update(unaffectedGroups=0,unaffectedBytes=0,calleeOnlyGroups=0)
            if reason=='eligible-for-further-proof':
                unit,f=matches[0];cw=int(f['writes'],16)
                all_writes=cw|int(c['setup'],16)|int(c['finish'],16)
                unaffected=[x for x in groups if not x&all_writes]
                row.update(calleeUnit=unit,calleeWrites=f['writes'],
                    unaffectedGroups=len(unaffected),unaffectedBytes=sum(x.bit_count() for x in unaffected),
                    calleeOnlyGroups=sum(not (x&cw) for x in groups))
            rows.append(row)
    return dict(compilerSha256=data['compilerSha256'],firmwareSha256=data['firmwareSha256'],
                codeBytes=receipt['codeBytes'],wideFunctions=sum(len(u['functions']) for u in data['units']),
                completeFunctions=sum(f['complete']=='1' for u in data['units'] for f in u['functions']),
                calls=len(rows),classifications=dict(reasons),
                totalSavedGroups=sum(r['savedGroups'] for r in rows),
                unaffectedGroups=sum(r['unaffectedGroups'] for r in rows),
                unaffectedBytes=sum(r['unaffectedBytes'] for r in rows),
                calleeOnlyGroups=sum(r['calleeOnlyGroups'] for r in rows),sites=rows)

def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('--builds',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args()
    results=read(a.builds/'results.json');cases=[]
    if results['status']!='passed':raise ValueError('complete qualification required')
    for c in results['cases']:
        if 'firmwareSha256' not in c:continue
        if c['status']!='passed':raise ValueError('unqualified case: '+c['id'])
        cases.append(dict(case=c['id'],**audit(a.builds/c['id']/'project/.build/mx251/latest')))
    value=dict(schema=1,mode='audit-only',codeGenerationChanged=False,
               limitation='may-write analysis only; excludes legacy bodies, unresolved calls and tail calls; no net-clobber or cycles proof',cases=cases)
    a.output.parent.mkdir(parents=True,exist_ok=True)
    a.output.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
    for c in cases:print(c['case'],c['completeFunctions'],'/',c['wideFunctions'],'wide bodies complete;',c['unaffectedGroups'],'of',c['totalSavedGroups'],'saved groups need further proof')
if __name__=='__main__':main()
