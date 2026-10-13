"""Exercise the dedicated ISP adapter without opening a serial port."""
import importlib.util
import io
import json
import sys
from types import SimpleNamespace
from stcgal.protocols import Stc15Protocol, StcProtocolException

spec = importlib.util.spec_from_file_location("guard", sys.argv[1])
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
data = open(sys.argv[2], "rb").read()
checks = []


def check(value, name):
    if not value:
        raise AssertionError(name)
    checks.append(name)


def fixture(version="7.2.5S", model="IAP15F2K61S2", bad_ack=False, variant=0x70):
    programmer = object.__new__(guard.GuardedStcGal)
    protocol = object.__new__(Stc15Protocol)
    events = []
    packets = []
    protocol.model = SimpleNamespace(name=model, code=62464)
    protocol.mcu_bsl_version = version
    protocol.status_packet = bytes(45) + bytes([variant])
    protocol._fixture_pulses = []
    protocol.pulse = lambda character, timeout: protocol._fixture_pulses.append((character, timeout))
    protocol.handshake = lambda: (events.append("handshake"), protocol.pulse(b"\xfe", 1))
    protocol.erase_flash = lambda size, capacity: events.append(("erase", size, capacity))
    protocol.write_packet = lambda packet: packets.append(packet)
    protocol.read_packet = lambda: bytes([0, 0]) if bad_ack else bytes([7 if packets[-1][0] == 7 else 2, 0x54])
    protocol.progress_cb = lambda *args: None
    protocol.program_options = lambda: events.append("options")
    protocol.disconnect = lambda: events.append("disconnect")
    programmer.protocol = protocol
    programmer.opts = SimpleNamespace(code_image=io.BytesIO(data))
    programmer.expected_model = "IAP15F2K61S2"
    programmer.expected_code_bytes = 62464
    programmer.expected_hash = guard.MONITOR_SETUP_SHA256
    programmer.clock_mode = "preserve"
    programmer.monitor_setup = True
    return programmer, events, packets


for version, model in [("7.2.4S", "IAP15F2K61S2"), ("7.2.5S", "IAP15L2K61S2")]:
    programmer, events, packets = fixture(version, model)
    try:
        programmer.program_mcu()
        raise AssertionError("unsupported target accepted")
    except StcProtocolException:
        check(not events and not packets, "unsupported BSL/model rejected before handshake and erase: " + version + "/" + model)

programmer, events, packets = fixture()
programmer.protocol.status_packet = bytes(45) + bytes([0x50])
try:
    programmer.program_mcu()
    raise AssertionError("different firmware variant accepted")
except StcProtocolException:
    check(not events and not packets, "different silicon/ISP marker rejected before erase")
programmer, events, packets = fixture()
programmer.program_mcu()
check(programmer.protocol._fixture_pulses == [(b"\x66", 1)], "monitor setup shares the verified calibration pulse adapter")
check(events == ["handshake", ("erase", 61440, 62464), "options", "disconnect"], "dedicated setup uses ISP lifecycle and preserves the options object")
check(packets[0][:5] == bytes([0x62, 0, 0, 0x5a, 0xa5]), "first encoded write is 62 0000 5AA5")
check(all(packet[0] == 0x42 for packet in packets[1:-1]), "remaining encoded writes use 42 rather than ordinary 02")
check(b"".join(packet[5:] for packet in packets[:-1]) == data, "all 61440 encoded bytes transmitted exactly once")
check(all(int.from_bytes(packet[1:3], "big") == index * 64 for index, packet in enumerate(packets[:-1])), "dedicated block addresses increase by 64 without wrapping")
check(packets[-1] == bytes([7, 0, 0, 0x5a, 0xa5]), "finish packet matches 7.2 ISP framing")
programmer, events, packets = fixture(bad_ack=True)
try:
    programmer.program_mcu()
    raise AssertionError("bad ACK accepted")
except StcProtocolException:
    check("options" not in events and len(packets) == 1, "failed write ACK stops programming before options and success")
result = dict(hardware=False, serial_ports_opened=0, count=len(checks), checks=checks)
with open(sys.argv[3], "w", encoding="utf-8") as output:
    json.dump(result, output, ensure_ascii=False, indent=2)
print("PASS %d dedicated monitor adapter checks; no serial access" % len(checks))
