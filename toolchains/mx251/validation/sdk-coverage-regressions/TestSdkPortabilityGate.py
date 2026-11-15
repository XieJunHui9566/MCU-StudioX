#!/usr/bin/env python3
"""Mutation tests of the read-only gate, using a caller-supplied real evidence manifest."""
import argparse
import copy
import hashlib
import importlib.util
import json
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--manifest', required=True)
    parser.add_argument('--output', required=True, help='Fresh test evidence directory')
    args = parser.parse_args()
    root = Path(args.output).resolve()
    if root.exists():
        raise SystemExit('Never overwrite earlier test evidence')
    root.mkdir(parents=True)
    checker = Path(__file__).with_name('RunSdkPortabilityGate.py')
    spec = importlib.util.spec_from_file_location('portability_gate', checker)
    gate = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(gate)
    original_path = Path(args.manifest).resolve()
    original = json.loads(original_path.read_text(encoding='utf-8-sig'))
    # Existing evidence pins stay absolute; no original file is edited/deleted.
    def absolute(node):
        if isinstance(node, dict):
            if 'path' in node and 'sha256' in node and not Path(node['path']).is_absolute():
                node['path'] = str((original_path.parent / node['path']).resolve())
            for value in node.values(): absolute(value)
        elif isinstance(node,list):
            for value in node: absolute(value)
    absolute(original)
    tests = []
    def attempt(name, mutation, expected):
        manifest = copy.deepcopy(original)
        mutation(manifest)
        p = root / (name + '.manifest.json')
        p.write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding='utf-8')
        result = gate.run(p)
        passed = result['offlineEvidenceStatus'] == expected and result['hardwareQualified'] is False and result['overallQualified'] is False
        (root / (name + '.result.json')).write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
        tests.append({'id':name,'passed':passed,'expectedOfflineStatus':expected,
                      'actualOfflineStatus':result['offlineEvidenceStatus'],'hardwareQualified':result['hardwareQualified'],
                      'errors':result.get('errors',[]),'result':str(root/(name+'.result.json'))})
    baseline = gate.run(original_path)
    if baseline['offlineEvidenceStatus'] not in {'PASS','PENDING'}:
        raise SystemExit('Base manifest is invalid; cannot use an already failing base for mutation qualification')
    attempt('unmeasured-time-stays-unqualified', lambda m: None, baseline['offlineEvidenceStatus'])
    attempt('missing-evidence-file', lambda m:m['evidence']['capacity'].update(path=str(root/'nonexistent.json')), 'FAIL')
    attempt('wrong-evidence-hash', lambda m:m['evidence']['capacity'].update(sha256='0'*64), 'FAIL')
    attempt('single-profile', lambda m:m.update(profiles=m['profiles'][:1]), 'FAIL')
    attempt('missing-evidence-role', lambda m:m.update(claims=[c for c in m['claims'] if c['role']!='cost-negative']), 'PENDING')
    attempt('missing-complete-project', lambda m:m.update(projects=m['projects'][:-1]), 'FAIL')
    attempt('forged-time-pass', lambda m:m['projects'][0]['time'].update(state='PASS',ratio=1.0), 'FAIL')
    attempt('forged-runtime-pass', lambda m:m['projects'][0]['correctness'].update(state='PASS'), 'FAIL')
    attempt('missing-time-reason', lambda m:m['projects'][0]['time'].pop('reason'), 'FAIL')
    attempt('missing-current-engine-binding', lambda m:[c.update(bindings=[]) for c in m['claims'] if c['role']=='gc-properties'], 'FAIL')
    attempt('null-is-not-proof', lambda m:m['claims'][0]['assertions'][0].update(value=None), 'FAIL')
    summary={'schema':'mx251.sdk-portability-mutations.v1','passed':all(x['passed'] for x in tests),
             'tests':tests,'count':len(tests),'hardwareAccess':False,'buildsExecuted':False,
             'checker':{'path':str(checker.resolve()),'sha256':hashlib.sha256(checker.read_bytes()).hexdigest()},
             'baseManifest':{'path':str(original_path),'sha256':hashlib.sha256(original_path.read_bytes()).hexdigest()},
             'scope':'Only manifest copies mutated; original evidence and candidate files untouched.'}
    (root/'results.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({'passed':summary['passed'],'count':len(tests),'failed':[x['id'] for x in tests if not x['passed']]}))
    return 0 if summary['passed'] else 1


if __name__=='__main__':
    raise SystemExit(main())
