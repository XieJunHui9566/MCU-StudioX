"""Bounded math XH3 replay, including observed R_BIT mode 0x529; unknown rejects.

Relocation arithmetic follows source/sdas/linksrc/lkrloc3.c relr3/adb_24_*.
This proves bytes and relocation inventory, not arbitrary source/ABI closure.
"""
from pathlib import Path
import argparse,hashlib,json,re
from collections import Counter,defaultdict
def archive_members(data):
    raise ValueError('This permanent pack uses extracted selected REL files only; archives are forbidden')

def sha(data):return hashlib.sha256(data).hexdigest()
def bound(e):
    p=Path(e['path']);b=p.read_bytes()
    if sha(b)!=e['sha256']:raise ValueError('hash mismatch: '+str(p))
    return b

def read_hex(text):
    mem={};base=0;ended=False
    for line in text.splitlines():
        if not line:continue
        if ended or not line.startswith(':'):raise ValueError('invalid HEX record')
        b=bytes.fromhex(line[1:])
        if len(b)!=b[0]+5 or sum(b)&255:raise ValueError('HEX checksum/size')
        n,addr,kind=b[0],int.from_bytes(b[1:3],'big'),b[3]
        if kind==0:
            for i,v in enumerate(b[4:4+n]):
                a=base+addr+i
                if a in mem:raise ValueError('overlap HEX')
                mem[a]=v
        elif kind==4:base=int.from_bytes(b[4:6],'big')<<16
        elif kind==2:base=int.from_bytes(b[4:6],'big')<<4
        elif kind==1:ended=True
        else:raise ValueError('unsupported HEX record')
    if not ended:raise ValueError('missing HEX EOF')
    return mem

def read_rel(data,name):
    lines=data.decode('ascii').splitlines();assert lines[0]=='XH3',name
    obj={'name':name,'module':'','sha256':sha(data),'areas':[],'symbols':[],'chunks':[]};area=None;t=None
    header=None
    for ln,line in enumerate(lines[1:],2):
        if not line or line.startswith(';'):continue
        tag,_,rest=line.partition(' ')
        if tag=='H':
            m=re.fullmatch(r'([\dA-F]+) areas ([\dA-F]+) global symbols',rest)
            if not m or header is not None:raise ValueError('invalid REL header')
            header=tuple(int(x,16)for x in m.groups())
        elif tag=='A':
            m=re.fullmatch(r'(\S+) size ([\dA-F]+) flags ([\dA-F]+) addr ([\dA-F]+)',rest)
            if not m:raise ValueError('invalid area')
            area=len(obj['areas']);obj['areas'].append(dict(name=m[1],size=int(m[2],16),flags=int(m[3],16),declared=int(m[4],16)))
        elif tag=='S':
            m=re.fullmatch(r'(\S+) (Def|Ref)([\dA-F]+)',rest)
            if not m:raise ValueError('invalid symbol')
            obj['symbols'].append(dict(name=m[1],binding=m[2],value=int(m[3],16),area=area if m[2]=='Def' else None))
        elif tag=='T':
            if t is not None:raise ValueError('unpaired T')
            t=bytes.fromhex(rest)
            if len(t)<3:raise ValueError('short T')
        elif tag=='R':
            r=bytes.fromhex(rest)
            if t is None or len(r)<4 or r[:2]!=b'\0\0':raise ValueError('invalid R')
            ai=int.from_bytes(r[2:4],'big');p=4;refs=[]
            if ai>=len(obj['areas']):raise ValueError('R area index')
            while p<len(r):
                mode=r[p];p+=1
                if mode&0xf0==0xf0:
                    if p>=len(r):raise ValueError('mode truncated')
                    mode=(mode&15)*256+r[p];p+=1
                if p+3>len(r):raise ValueError('relocation truncated')
                refs.append(dict(mode=mode,index=r[p],target=int.from_bytes(r[p+1:p+3],'big'),line=ln));p+=3
            obj['chunks'].append(dict(area=ai,offset=int.from_bytes(t[:3],'big'),raw=list(t),refs=refs));t=None
        elif tag=='M':obj['module']=rest
        elif tag!='O':raise ValueError('unsupported record '+tag)
    if t is not None or header!=(len(obj['areas']),len(obj['symbols'])):raise ValueError('incomplete object')
    return obj

def parse_map(text):
    areas={};symbols={}
    for line in text.splitlines():
        m=re.match(r'^(\S+)\s+([0-9A-F]{8})\s+([0-9A-F]{8})\s+=',line)
        if m:
            v=(int(m[2],16),int(m[3],16))
            if m[1] in areas and areas[m[1]]!=v:raise ValueError('ambiguous map area')
            areas[m[1]]=v
        m=re.match(r'^\s*(?:[A-Z]:\s*)?([0-9A-F]{8})\s+(\S+)(?:\s+\S+)?\s*$',line)
        if m:
            v=int(m[1],16)
            if m[2] in symbols and symbols[m[2]]!=v:raise ValueError('ambiguous map symbol '+m[2])
            symbols[m[2]]=v
    for n,v in symbols.items():
        if n.startswith('s_') and 'l_'+n[2:] in symbols:areas.setdefault(n[2:],(v,symbols['l_'+n[2:]]))
    return areas,symbols

def place(objects,mapareas,symbols,trace=None):
    cursor=Counter();evidence=[]
    for oi,obj in enumerate(objects):
        for i,a in enumerate(obj['areas']):
            anchors={symbols[s['name']]-s['value'] for s in obj['symbols'] if s['binding']=='Def' and s['area']==i and s['name'] in symbols}
            # Header zero is the linker's synthetic absolute-symbol header.
            traced=trace.get((oi+1,obj['module'],i)) if trace else None
            if traced:
                if traced[0]!=a['name']:raise ValueError('traced area identity mismatch')
                base=traced[1]
            elif a['flags']&8:base=0
            elif a['flags']&32:
                if a['name'] not in mapareas:raise ValueError('missing code area '+a['name'])
                base=mapareas[a['name']][0]+(0 if a['flags']&4 else cursor[a['name']])
            elif len(anchors)==1:base=next(iter(anchors))
            elif not a['size'] or a['flags']&4:base=mapareas.get(a['name'],(0,0))[0]
            else:base=None
            if anchors and (len(anchors)!=1 or base not in anchors):raise ValueError('area placement anchors disagree '+obj['name']+' '+a['name'])
            a['base']=base
            cursor[a['name']]+=a['size'] if not a['flags']&4 else 0
            evidence.append(dict(object=obj['name'],**a))
        for s in obj['symbols']:
            if s['binding']=='Def' and s['name'] in symbols:
                ab=0 if s['area'] is None else obj['areas'][s['area']]['base']
                if ab is not None and ab+s['value']!=symbols[s['name']]:raise ValueError('symbol placement mismatch '+s['name'])
    return evidence

def relocate(chunk,obj,symbols):
    raw=chunk['raw'][:];keep=[True]*len(raw);refs=[];area=obj['areas'][chunk['area']]
    if area['base'] is None:raise ValueError('unknown source area '+obj['name']+' '+area['name'])
    pc=area['base']+chunk['offset'];removed=0;occupied=set()
    for row in chunk['refs']:
        m,p,ix=row['mode'],row['index'],row['target'];sym=bool(m&2)
        table=obj['symbols'] if sym else obj['areas']
        if ix>=len(table):raise ValueError('relocation target index')
        target=table[ix];base=symbols.get(target['name']) if sym else target['base']
        if base is None:raise ValueError('unknown relocation target '+target['name'])
        # Only modes used by our target profile; other modes fail closed.
        if m not in {0,2,0x80,0x82,0x800,0x802,0x121,0x123,0x109,0x10b,0x189,0x18b,0x309,0x30b,0x301,0x303,0x101,0x103,0x181,0x183,0x9,0xb,0x89,0x8b,0x5,0x7,0x4,0x6,0x529}:
            raise ValueError('unsupported relocation mode '+hex(m))
        width=(3 if m&0x100 or not(m&1)and m&0x89==0x80 else 2 if not(m&1) or m&8 else 1)
        if p<3 or p+width>len(raw)or any(x in occupied for x in range(p,p+width)):raise ValueError('relocation bounds/overlap')
        occupied.update(range(p,p+width));addend=int.from_bytes(bytes(raw[p:p+width]),'big')
        operand_pc=pc+p-3-removed;val=base+addend
        if m&4:val-=operand_pc+(1 if m&1 else 2)
        if m&0x800 and val&0xff0000!=(operand_pc+2)&0xff0000:raise ValueError('64K control region')
        if m&4 and m&1 and not(-128<=val<=127):raise ValueError('PCR overflow')
        raw[p:p+width]=list((val&((1<<(8*width))-1)).to_bytes(width,'big'))
        if m == 0x529:
            # ASlink adb_24_bit: add 24-bit relocation first; low byte is
            # byte address, low3 bits of middle byte are bit index.
            byte_address=val & 0xff
            if 0x20 <= byte_address <= 0x2f:
                bit_base=(byte_address-0x20)*8
            elif byte_address >= 0x80 and not(byte_address & 7):
                bit_base=byte_address
            else:
                raise ValueError('invalid bit-addressable byte '+hex(byte_address))
            raw[p+2]=bit_base+((val >> 8)&7)
        selected=None
        if m&1 and (m&0x100 or m&8):
            selected=0 if m&0x200 else width-2 if m&0x80 else width-1
            for k in range(width):keep[p+k]=(k==selected)
            removed+=width-1
        refs.append(dict(**row,object=obj['name'],source_area=area['name'],target_kind='symbol' if sym else 'area',target_name=target['name'],target_base=base,addend=addend,target_value=base+addend,operand_pc=operand_pc,width=width,selected_byte=selected,emitted_width=1 if selected is not None else width))
    data=bytes(v for k,v in enumerate(raw)if k>=3 and keep[k])
    return pc,data,refs

def run(session_path,placement_path=None):
    s=json.loads(Path(session_path).read_text(encoding='utf-8-sig'));link=s['link'];objects=[]
    # Binding checks do not claim semantic coverage.
    for e in link['direct_objects']:objects.append(read_rel(bound(e),str(Path(e['path']))))
    ars={str(Path(e['path'])):archive_members(bound(e))for e in link['archives']}
    for e in link['selected_members']:
        ms=[m for m in ars[str(Path(e['archive_path']))]if m['name']==e['member_name'] and m['offset']==e['member_offset'] and m['sha256']==e['member_sha256']]
        if len(ms)!=1:raise ValueError('member identity mismatch')
        objects.append(read_rel(ms[0]['data'],e['archive_path']+'!'+e['member_name']))
    trace={}
    if placement_path:
        for line in Path(placement_path).read_text().splitlines():
            if not line.startswith('MX251-PLACE\t'):continue
            _,oi,mod,idx,name,base,size=line.split('\t');key=(int(oi),mod,int(idx));val=(name,int(base),int(size))
            if key in trace and trace[key]!=val:raise ValueError('inconsistent actual placement')
            trace[key]=val
        if not trace:raise ValueError('empty placement trace')
    ma,sy=parse_map(bound(link['map']).decode('ascii'));placements=place(objects,ma,sy,trace)
    image=read_hex(bound(link['image']).decode('ascii'));memory={};refs=[];counts=Counter()
    for obj in objects:
        for c in obj['chunks']:
            a=obj['areas'][c['area']]
            if len(c['raw'])==3 and not c['refs']:continue
            pc,data,rr=relocate(c,obj,sy);refs.extend(rr)
            if not a['flags']&32:
                if data:raise ValueError('unexpected initialized non-CODE data')
                continue
            for i,v in enumerate(data):
                addr=pc+i
                if addr in memory:raise ValueError('overlap reconstructed code')
                memory[addr]=v
            counts[obj['name']]+=len(data)
    missing=set(image)-set(memory);extra=set(memory)-set(image)
    mismatch=[a for a,v in memory.items()if a in image and image[a]!=v]
    if missing or extra or mismatch:raise ValueError(f'image mismatch missing={len(missing)} extra={len(extra)} changed={len(mismatch)} first={mismatch[:5]}')
    return dict(schema='mx251.calls-link-proof.v1',status='exact-linked-byte-match',abi_changes_allowed=False,scope='Actual selected objects and supported XH3 modes only',session=str(Path(session_path).resolve()),image_sha256=link['image']['sha256'],byte_count=len(memory),objects=[dict(name=o['name'],sha256=o['sha256'],bytes=counts[o['name']])for o in objects],placements=placements,symbols=sy,relocations=refs,modes=dict(Counter(hex(r['mode'])for r in refs)),remaining=['Per-target control/address-reference classification','Source/effective signature and boundary proof','Transformation correctness and final image revalidation'])

if __name__=='__main__':
    ap=argparse.ArgumentParser();ap.add_argument('--session',required=True);ap.add_argument('--output',required=True);ap.add_argument('--placements');a=ap.parse_args()
    try:r=run(a.session,a.placements)
    except Exception as e:r=dict(status='rejected',error=type(e).__name__+': '+str(e),abi_changes_allowed=False)
    Path(a.output).write_text(json.dumps(r,indent=2),encoding='utf-8');print(json.dumps({k:v for k,v in r.items()if k in ['status','error','byte_count','modes']}));raise SystemExit(0 if r['status']=='exact-linked-byte-match' else 1)
