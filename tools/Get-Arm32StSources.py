"""Download pinned official ST CMSIS device repositories for the expansion recipes."""
import argparse, concurrent.futures, hashlib, json, urllib.request
from pathlib import Path

def fetch(url):
    with urllib.request.urlopen(urllib.request.Request(url,headers={'User-Agent':'MCU-StudioX-Maintainer/1.0'}),timeout=60) as response:
        return response.read(),response.url

def one(family,root):
    repo='STMicroelectronics/cmsis-device-'+family
    tags,_=fetch('https://api.github.com/repos/'+repo+'/tags?per_page=1')
    tag=json.loads(tags)[0]; commit=tag['commit']['sha']
    url='https://codeload.github.com/'+repo+'/zip/'+commit
    path=root/(family+'-'+commit+'.zip')
    if not path.exists():
        content,_=fetch(url);path.write_bytes(content)
    return dict(family=family,repository='https://github.com/'+repo,tag=tag['name'],commit=commit,url=url,file=path.name,sha256=hashlib.sha256(path.read_bytes()).hexdigest())

def main():
    p=argparse.ArgumentParser();p.add_argument('--output',type=Path,required=True);a=p.parse_args();a.output.mkdir(parents=True,exist_ok=True)
    results=[]
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        futures=[pool.submit(one,f,a.output) for f in ['f0','f2','f3','f7','g0','g4','l0','l1','l4','h7','c0','u0']]
        for future in concurrent.futures.as_completed(futures):
            r=future.result();results.append(r);print(r['family'],r['tag'],r['commit'],flush=True)
            (a.output/'sources.json').write_text(json.dumps(sorted(results,key=lambda x:x['family']),indent=2),encoding='utf-8')
if __name__=='__main__':main()
