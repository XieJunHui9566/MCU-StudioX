"""Bound direct jobs and their inherited process trees; write start/exit records."""
import ctypes as C
from ctypes import wintypes as W
import json,subprocess,time
from pathlib import Path
K=C.WinDLL('kernel32',use_last_error=True)
K.CreateJobObjectW.argtypes=[C.c_void_p,W.LPCWSTR];K.CreateJobObjectW.restype=W.HANDLE
K.AssignProcessToJobObject.argtypes=[W.HANDLE,W.HANDLE];K.AssignProcessToJobObject.restype=W.BOOL
K.QueryInformationJobObject.argtypes=[W.HANDLE,C.c_int,C.c_void_p,W.DWORD,C.c_void_p];K.QueryInformationJobObject.restype=W.BOOL
K.SetInformationJobObject.argtypes=[W.HANDLE,C.c_int,C.c_void_p,W.DWORD];K.SetInformationJobObject.restype=W.BOOL
K.TerminateJobObject.argtypes=[W.HANDLE,W.UINT];K.TerminateJobObject.restype=W.BOOL
K.CloseHandle.argtypes=[W.HANDLE];K.CloseHandle.restype=W.BOOL
class Accounting(C.Structure):
 _fields_=[('user',C.c_int64),('kernel',C.c_int64),('periodUser',C.c_int64),('periodKernel',C.c_int64),('faults',W.DWORD),('total',W.DWORD),('active',W.DWORD),('terminated',W.DWORD)]
class BasicLimit(C.Structure):
 _fields_=[('processTime',C.c_int64),('jobTime',C.c_int64),('flags',W.DWORD),('minWorking',C.c_size_t),('maxWorking',C.c_size_t),('activeLimit',W.DWORD),('affinity',C.c_size_t),('priority',W.DWORD),('scheduling',W.DWORD)]
class ExtendedLimit(C.Structure):
 _fields_=[('basic',BasicLimit),('io',C.c_uint64*6),('processMemory',C.c_size_t),('jobMemory',C.c_size_t),('peakProcess',C.c_size_t),('peakJob',C.c_size_t)]
def run(cmd,cwd,env,out,err,record,timeout=120):
 cmd=list(map(str,cmd));record=Path(record);started=time.time();job=K.CreateJobObjectW(None,None)
 if not job:raise C.WinError(C.get_last_error())
 limits=ExtendedLimit();limits.basic.flags=0x2000
 if not K.SetInformationJobObject(job,9,C.byref(limits),C.sizeof(limits)):
  error=C.get_last_error();K.CloseHandle(job);raise C.WinError(error)
 row=dict(argv=cmd,cwd=str(cwd),started=started,timeout=timeout)
 q=None
 try:
  with Path(out).open('wb') as stdout,Path(err).open('wb') as stderr:
   q=subprocess.Popen(cmd,cwd=cwd,env=env,stdout=stdout,stderr=stderr)
   row['pid']=q.pid;row['status']='RUNNING';record.write_text(json.dumps(row,indent=2),encoding='utf8')
   if not K.AssignProcessToJobObject(job,W.HANDLE(q._handle)):
    if q.poll() is None:
     q.kill();q.wait(timeout=10);raise C.WinError(C.get_last_error())
    row['jobAssignment']='already-exited'
   else:row['jobAssignment']='assigned'
   try:q.wait(timeout=timeout)
   except subprocess.TimeoutExpired:
    row['timedOut']=True;K.TerminateJobObject(job,124);q.wait(timeout=15)
   info=Accounting()
   if not K.QueryInformationJobObject(job,1,C.byref(info),C.sizeof(info),None):raise C.WinError(C.get_last_error())
   if info.active:
    row['unexpectedLiveChildren']=int(info.active);K.TerminateJobObject(job,125)
    for _ in range(100):
     K.QueryInformationJobObject(job,1,C.byref(info),C.sizeof(info),None)
     if not info.active:break
     time.sleep(.05)
   row.update(exitCode=q.returncode,seconds=time.time()-started,jobProcesses=int(info.total),activeChildren=int(info.active),status='EXITED')
   record.write_text(json.dumps(row,indent=2),encoding='utf8')
   if row.get('timedOut') or row.get('unexpectedLiveChildren') or info.active:raise RuntimeError('bounded child failed: '+str(record))
   return row
 finally:
  if q is not None and q.poll() is None:K.TerminateJobObject(job,126);q.wait(timeout=15)
  K.CloseHandle(job)
