"""验证精确校准适配范围及保留超时/返回/错误；不打开串口。"""
import importlib.util
import hashlib
import io
import json
import sys
from types import SimpleNamespace
from stcgal.protocols import Stc15Protocol, StcProtocolException
from stcgal.frontend import StcGal

spec = importlib.util.spec_from_file_location("studiox_guard", sys.argv[1])
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
checks=[]


def check(value, name):
    if not value: raise AssertionError(name)
    checks.append(name)


def fixture(model="IAP15F2K61S2", capacity=62464, version="7.2.5S", marker=0x70, external=False):
    protocol=object.__new__(Stc15Protocol)
    protocol.model=SimpleNamespace(name=model,code=capacity)
    protocol.mcu_bsl_version=version
    protocol.status_packet=bytes(45)+bytes([marker])
    protocol.external_clock=external
    calls=[]
    def original(character=b"\x7f",timeout=0):
        calls.append((character,timeout))
        if timeout==99: raise StcProtocolException("original failure")
        return "unchanged result"
    protocol.pulse=original
    protocol._fixture_packets=[]
    protocol.write_packet=lambda data,*args,**kwargs:protocol._fixture_packets.append((data,args,kwargs))
    return protocol,calls,original


for values in [dict(model="IAP15L2K61S2"),dict(capacity=61440),dict(version="7.2.4S"),
               dict(marker=0x50),dict(external=True)]:
    protocol,calls,original=fixture(**values)
    check(not guard.configure_iap15_calibration(protocol) and protocol.pulse is original,
          "unverified target/BSL/marker or external clock keeps original protocol: "+str(values))
check(not guard.configure_iap15_calibration(SimpleNamespace()),"other protocol type is untouched")
protocol,calls,original=fixture()
protocol.status_packet=b"\x70"
check(not guard.configure_iap15_calibration(protocol),"short status is rejected")
protocol,calls,original=fixture()
check(guard.configure_iap15_calibration(protocol),"verified internal-RC IAP15 profile is enabled")
wrapped=protocol.pulse
check(guard.configure_iap15_calibration(protocol) and protocol.pulse is wrapped,"adapter installation is idempotent")
check(protocol.pulse(b"\xfe",1)=="unchanged result" and calls==[(b"\x66",1)],"only FE changes to 66; timeout and return are preserved")
protocol.write_packet(guard.STCGAL_FIRST_CALIBRATION,epilogue_len=2)
protocol.write_packet(bytes.fromhex("000c01020304"))
check(protocol._fixture_packets==[(guard.IAP15_FIRST_CALIBRATION,(),{"epilogue_len":2}),(bytes.fromhex("000c01020304"),(),{})],
      "only exact first calibration request changes; subsequent requests and write arguments are preserved")
check(guard.IAP15_FIRST_CALIBRATION[1]==11 and guard.IAP15_FIRST_CALIBRATION[-2:]==b"\x00\x00" and
      guard.IAP15_FIRST_CALIBRATION[2:-2]==guard.STCGAL_FIRST_CALIBRATION[2:-2],
      "official first round contains 11 identical candidates and two padding bytes")
protocol.pulse()
protocol.pulse(b"\x7f",2)
protocol.pulse(b"\x66",3)
check(calls[1:]==[(b"\x7f",0),(b"\x7f",2),(b"\x66",3)],"startup synchronization and other pulses remain unchanged")
try:
    protocol.pulse(b"\xfe",99)
    raise AssertionError("original error was swallowed")
except StcProtocolException as error:
    check(str(error)=="original failure","original protocol error is propagated")
programmer=object.__new__(guard.GuardedStcGal)
protocol,calls,original=fixture()
protocol.options=SimpleNamespace(get_clock_source=lambda:"internal")
programmer.protocol=protocol
image=b"offline image"
programmer.opts=SimpleNamespace(code_image=io.BytesIO(image),trim=0.0)
programmer.expected_model="IAP15F2K61S2"
programmer.expected_code_bytes=62464
programmer.expected_hash=hashlib.sha256(image).hexdigest().upper()
programmer.clock_mode="preserve"
programmer.monitor_setup=False
original_program=StcGal.program_mcu
try:
    StcGal.program_mcu=lambda self:self.protocol.pulse(b"\xfe",1)
    programmer.program_mcu()
    check(calls==[(b"\x66",1)],"ordinary download installs adapter after image/model guards")
finally:
    StcGal.program_mcu=original_program
result=dict(hardware=False,serial_ports_opened=0,count=len(checks),checks=checks)
if len(sys.argv)>2:
    with open(sys.argv[2],"w",encoding="utf-8") as file:json.dump(result,file,ensure_ascii=False,indent=2)
print("PASS %d calibration adapter checks; no serial access" % len(checks))
