"""Pure receiver contract against the established independent MCU oracle."""
import statistics,copy
def validate_mcu(decoded,oracle,hz):
 assert hz>0 and decoded['badChecksums']==0
 records=decoded['records'];assert decoded['uniqueRecords']==len(records)==1179
 by={(q['id']+((q['modeRaw']>>1)<<8),q['sample']):q for q in records}
 assert len(by)==1179 and len(oracle['expected'])==1152 and len(oracle['names'])==9
 expectedkeys={(k,0) for k in range(1152)}|{(1152+k,s) for k in range(9) for s in range(3)}
 assert set(by)==expectedkeys
 for k,e in enumerate(oracle['expected']):
  q=by[k,0]
  assert int(q['result'],16)==e['expected'] and int(q['remainder'],16)==0
  assert q['status']==58 and q['repeats']==1 and not q['overflow'] and q['ticks']==0 and q['modeRaw']&1
 times=[]
 for k,name in enumerate(oracle['names']):
  samples=[by[1152+k,s] for s in range(3)]
  assert all(int(q['result'],16)==oracle['timing']['expected'][k] and int(q['remainder'],16)==0 and q['status']==59 and q['repeats']==8 and not q['overflow'] and q['modeRaw']&1 for q in samples)
  ticks=[q['ticks'] for q in samples];median=statistics.median(ticks)
  times.append(dict(name=name,ticks=ticks,medianTicks=median,repeats=8,systemClocksPerCall=median*12/8,microsecondsPerCall=median*12e6/hz/8))
 return dict(complete=True,allResultsValid=True,correctnessCases=1152,passed=1152,frames=decoded['frames'],uniqueRecords=1179,timings=times,receptionIsNotExecutionCount=True)
def selftest(decoded,oracle,hz):
 validate_mcu(decoded,oracle,hz);cases=[]
 for name,mutate in [('missing',lambda d:d['records'].pop()),('duplicate',lambda d:d['records'].__setitem__(1,dict(d['records'][0]))),('checksum',lambda d:d.update(badChecksums=1)),('wrong-result',lambda d:d['records'][0].update(result='FFFFFFFF')),('wrong-remainder',lambda d:d['records'][0].update(remainder='00000001')),('overflow',lambda d:d['records'][-1].update(overflow=1)),('repeat',lambda d:d['records'][-1].update(repeats=1)),('status',lambda d:d['records'][0].update(status=59))]:
  d=copy.deepcopy(decoded);mutate(d)
  try:validate_mcu(d,oracle,hz)
  except (AssertionError,KeyError):cases.append(name)
  else:raise AssertionError(name)
 return dict(passed=True,negativeCases=cases)


def validate_protocol(decoded,oracle,hz):
 assert hz>0 and decoded['badChecksums']==0
 records=decoded['records'];assert decoded['uniqueRecords']==len(records)==521
 by={q['id']+((q['modeRaw']>>1)<<8):q for q in records};assert set(by)==set(range(521))
 assert all(q['sample']==0 and q['repeats']==1 and not q['overflow'] and q['modeRaw']&1 and int(q['remainder'],16)==0 for q in records)
 expected=[v for row in oracle['expected'] for v in row];assert len(expected)==512
 for k,e in enumerate(expected):
  q=by[k];assert int(q['result'],16)==e and q['status']==69 and q['ticks']==0
 times=[]
 for k,c in enumerate(oracle['timingCases']):
  samples=[by[512+k*3+s] for s in range(3)]
  assert all(int(q['result'],16)==oracle['expected'][c][4] and q['status']==70 for q in samples)
  ticks=[q['ticks'] for q in samples];median=statistics.median(ticks)
  times.append(dict(name='case-'+str(c),ticks=ticks,medianTicks=median,repeats=1,systemClocksPerCall=median*12,microsecondsPerCall=median*12e6/hz))
 return dict(complete=True,allResultsValid=True,correctnessCases=512,passed=512,frames=decoded['frames'],uniqueRecords=521,timings=times,receptionIsNotExecutionCount=True)
