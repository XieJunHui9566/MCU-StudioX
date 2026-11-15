"""Compile the shared effects header as C17 with warnings fatal; retain evidence."""
import argparse,hashlib,json,subprocess
from pathlib import Path
p=argparse.ArgumentParser(description=__doc__)
for n in ('compiler-source','gcc','output'):p.add_argument('--'+n,type=Path,required=True)
a=p.parse_args();out=a.output.resolve();out.mkdir(parents=True,exist_ok=False)
source=Path(__file__).with_name('InstructionEffects.c');header=a.compiler_source.resolve()/'src/mcs251/instruction_effects.h'
commands=[[str(a.gcc.resolve()),'-std=c17','-Wall','-Wextra','-Werror','-I',str(header.parent),str(source),'-o',str(out/'effects.exe')],[str(out/'effects.exe')]]
records=[]
for i,cmd in enumerate(commands):
    r=subprocess.run(cmd,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=60)
    (out/f'{i}.stdout.log').write_bytes(r.stdout);(out/f'{i}.stderr.log').write_bytes(r.stderr)
    records.append(dict(command=cmd,exitCode=r.returncode))
    if r.returncode:raise RuntimeError(r.stderr.decode(errors='replace'))
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
(out/'result.json').write_text(json.dumps(dict(passed=True,sourceSha256=sha(source),headerSha256=sha(header),compilerSha256=sha(a.gcc),commands=records),indent=2)+'\n',encoding='utf8')
print('instruction effects shared-header checks passed')
