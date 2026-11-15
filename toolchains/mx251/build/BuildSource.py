from pathlib import Path
import argparse,hashlib,json,os,subprocess,time
ap=argparse.ArgumentParser(description='Build the pinned Windows MCS51/MCS251 source state; no frozen objects.')
for name in ('source','output','host-deps','gcc-bin','gawk','cmake','ninja'):
 ap.add_argument('--'+name,type=Path,required=True)
a=ap.parse_args();S=a.source.resolve();B=a.output.resolve();D=a.host_deps.resolve();G=a.gcc_bin.resolve()
awk=a.gawk.resolve();cmake=a.cmake.resolve();ninja=a.ninja.resolve()
if S==B or S in B.parents:raise ValueError('Build output must be outside source state')
for path in (S/'sdccconf.h',S/'src/version.h',G/'gcc.exe',G/'g++.exe',D/'usr/bin/bison.exe',D/'usr/bin/flex.exe',awk,cmake,ninja):
 if not path.is_file():raise FileNotFoundError(path)
B.mkdir(parents=True,exist_ok=False);gen=B/'generated';gen.mkdir();(gen/'mcs251').mkdir();(gen/'mcs51').mkdir()
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
env=os.environ.copy()
for key in ('CPATH','C_INCLUDE_PATH','CPLUS_INCLUDE_PATH','LIBRARY_PATH','COMPILER_PATH','GCC_EXEC_PREFIX'):env.pop(key,None)
env['PATH']=';'.join(map(str,[D/'usr/bin',awk.parent,G,Path(os.environ['SystemRoot'])/'System32']))
posix=lambda p:'/'+p.as_posix()[0].lower()+p.as_posix()[2:]
env['BISON_PKGDATADIR']=posix(D/'usr/share/bison');env['M4']=posix(D/'usr/bin/m4.exe');env['SOURCE_DATE_EPOCH']='0'
commands=[]
def run(label,cmd,cwd=B,stdout=None):
 cmd=list(map(str,cmd));start=time.time()
 with (stdout or B/(label+'.stdout.log')).open('wb') as out, (B/(label+'.stderr.log')).open('wb') as err:
  q=subprocess.run(cmd,cwd=cwd,env=env,stdout=out,stderr=err)
 commands.append(dict(label=label,args=cmd,cwd=str(cwd),exitCode=q.returncode,seconds=time.time()-start))
 (B/'commands.json').write_text(json.dumps(commands,indent=2),encoding='utf8')
 if q.returncode:raise RuntimeError(label+' failed: '+(B/(label+'.stderr.log')).read_text(errors='replace')[-2000:])
run('bison',[D/'usr/bin/bison.exe','-y','-d','-o','SDCCy.c',S/'src/SDCC.y'],gen)
run('flex',[D/'usr/bin/flex.exe','-8','-f','-t',S/'src/SDCC.lex'],gen,gen/'SDCClex.c')
for port in ('mcs51','mcs251'):
 defs=[S/'src/mcs51/peeph.def']+([S/'src/mcs251/peeph-mcs251.def'] if port=='mcs251' else [])
 run('peeph-'+port,[awk,'-f',S/'src/SDCCpeeph.awk',*defs],stdout=gen/port/'peeph.rul')
core='SDCChasht SDCCmain SDCCattr SDCCsymt SDCCopt SDCCast SDCCmem SDCCval SDCCicode SDCCbitv SDCCset SDCClabel SDCCBBlock SDCCloop SDCCcse SDCCcflow SDCCdflow SDCClrange SDCCptropt SDCCpeeph SDCCglue SDCCasm SDCCmacro SDCCutil SDCCdebug cdbFile SDCCdwarf2 SDCCerr SDCCsystem SDCCgen'.split()
sources=[S/'src'/(n+'.c') for n in core]+list((S/'src').glob('*.cc'))+[S/'support/util'/(n+'.c') for n in ('NewAlloc','dbuf','dbuf_string','findme')]+[gen/'SDCCy.c',gen/'SDCClex.c'];assert len(sources)==40
quote=lambda p:'"'+Path(p).as_posix()+'"'
cm=['cmake_minimum_required(VERSION 3.20)','project(MX251Clean LANGUAGES C CXX)','set(CMAKE_C_STANDARD 17)','set(CMAKE_CXX_STANDARD 11)','add_compile_options(-O2 -Wall -Wno-parentheses)','add_link_options(-static -Wl,--no-insert-timestamp)',
 'include_directories('+ ' '.join(quote(p) for p in (gen,S/'src',S,S/'support/util',D/'mingw64/include'))+')']
inputs=list(sources)
for port,names in [('mcs251','gen main peep ralloc rtrack frame wide'),('mcs51','gen main peep ralloc rtrack')]:
 files=[S/'src'/port/(n+'.c') for n in names.split()];inputs+=files
 cm+=['add_library('+port+' STATIC '+' '.join(map(quote,files))+')','target_include_directories('+port+' PRIVATE '+quote(gen/port)+' '+quote(S/'src'/port)+')']
cm+=['add_executable(sdcc '+' '.join(map(quote,sources))+')','target_link_libraries(sdcc PRIVATE mcs251 mcs51 m)']
asfiles=[S/'sdas/as251'/(n+'.c') for n in ('mcs251pst','mcs251mch','mcs251adr')]+[S/'sdas/asxxsrc'/(n+'.c') for n in 'asdbg asdata asexpr aslex aslist asmain asout assubr assym asmcro sdas strcmpi'.split()]+[S/'support/util'/(n+'.c') for n in ('dbuf','dbuf_string')]
cm+=['add_executable(sdas251 '+' '.join(map(quote,asfiles))+')','target_include_directories(sdas251 PRIVATE '+quote(S/'sdas/as251')+' '+quote(S/'sdas/asxxsrc')+')','target_compile_definitions(sdas251 PRIVATE SDCDB NOICE INDEXLIB)','target_link_libraries(sdas251 PRIVATE m)']
lkfiles=[S/'sdas/linksrc'/(n+'.c') for n in 'lk_readnl lkaomf51 lkar lkarea lkdata lkelf lkeval lkhead lklex lklib lklibr lklist lkmain lkmem lknoice lkout lkrel lkrloc lkrloc3 lksdcclib lksym sdld lksdcdb lkbank'.split()]+[S/'sdas/asxxsrc/strcmpi.c']
cm+=['add_executable(sdld '+' '.join(map(quote,lkfiles))+')','target_include_directories(sdld PRIVATE '+quote(S/'sdas/linksrc')+' '+quote(S/'sdas')+')','target_compile_definitions(sdld PRIVATE INDEXLIB UNIX)','target_link_libraries(sdld PRIVATE m)']
inputs+=asfiles+lkfiles
(B/'CMakeLists.txt').write_text('\n'.join(cm)+'\n',encoding='utf8')
run('configure',[cmake,'-S',B,'-B',B/'build','-G','Ninja','-DCMAKE_MAKE_PROGRAM='+str(ninja),'-DCMAKE_C_COMPILER='+str(G/'gcc.exe'),'-DCMAKE_CXX_COMPILER='+str(G/'g++.exe')])
run('compile',[cmake,'--build',B/'build','--parallel','2'])
run('version',[B/'build/sdcc.exe','--version'])
tools=[G/'gcc.exe',G/'g++.exe',D/'usr/bin/bison.exe',D/'usr/bin/flex.exe',awk,cmake,ninja]
(B/'input-lock.json').write_text(json.dumps([dict(path=str(p),sha256=sha(p)) for p in sorted(set(inputs+tools+[p for p in S.rglob('*') if p.is_file()]))],indent=2),encoding='utf8')
(B/'result.json').write_text(json.dumps(dict(sourceState=str(S),historicalObjectsUsed=False,outputs=[dict(path=str(B/'build'/n),sha256=sha(B/'build'/n)) for n in ('sdcc.exe','sdas251.exe','sdld.exe')]),indent=2),encoding='utf8')
print('clean source build complete',flush=True)
