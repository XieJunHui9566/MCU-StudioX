"""Reproduce S01-S07 without hardware I/O; optionally replay a read-only status capture."""
import argparse
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import re
import struct
import sys
from types import SimpleNamespace

sys.dont_write_bytecode = True
parser = argparse.ArgumentParser()
parser.add_argument("--guard")
parser.add_argument("--output", required=True)
parser.add_argument("--status")
args = parser.parse_args()
import serial
attempted_io = []
def deny_io(*_args, **_kwargs):
    attempted_io.append("serial open")
    raise AssertionError("Offline checks cannot open serial devices")
serial.Serial.open = deny_io
serial.serial_for_url = deny_io
from stcgal import options as opt, protocols as proto, frontend
from stcgal.models import MCUModelDatabase as DB
if args.guard:
    spec = importlib.util.spec_from_file_location("studiox_guard", args.guard)
    guard = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(guard)

checks = []
details = {}
quiet = io.StringIO()
def check(condition, label, evidence=None):
    checks.append({"passed": bool(condition), "name": label, "evidence": evidence})
def rejected(action, label, unchanged=None):
    try:
        action()
        check(False, label, "accepted")
    except (ValueError, proto.StcProtocolException) as error:
        check(unchanged is None or unchanged(), label, type(error).__name__ + ": " + str(error))
    except Exception as error:
        check(False, label, type(error).__name__ + ": " + str(error))

sizes = {opt.Stc12AOption: 4, opt.Stc12Option: 4, opt.Stc15AOption: 13, opt.Stc15Option: 5, opt.Stc8Option: 5}
def make(cls, seed=0xff):
    return cls(seed if cls is opt.Stc89Option else bytes([seed] * sizes[cls]))

with contextlib.redirect_stdout(quiet), contextlib.redirect_stderr(quiet):
    roundtrips = 0
    failures = {}
    api_failures = {}
    for cls in (opt.Stc89Option, *sizes):
        for seed in range(256):
            original = make(cls, seed)
            for name, getter, _ in original.options:
                value = getter()
                try:
                    if original.get_option(name) != value:
                        raise AssertionError("getter mismatch")
                except Exception as error:
                    api_failures[cls.__name__ + "." + name] = type(error).__name__
                if name == "cpu_core_voltage" and value == "unknown":
                    continue
                if name == "program_eeprom_split" and not (512 <= value <= 65024 and value % 512 == 0):
                    continue
                clone = make(cls, seed)
                roundtrips += 1
                try:
                    setter = next(s for n, _, s in clone.options if n == name)
                    setter(str(value) if isinstance(value, int) and not isinstance(value, bool) else value)
                    if next(g for n, g, _ in clone.options if n == name)() != value or clone.get_msr() != original.get_msr():
                        raise AssertionError("option bits changed")
                except Exception as error:
                    key = cls.__name__ + "." + name
                    failures[key] = failures.get(key, 0) + 1
    check(not failures, "S01/S02: six option classes preserve every unrelated bit on getter-setter roundtrip", failures)
    check(not api_failures, "S05: named getter API works for all registered options", api_failures)
    details["roundtrips"] = roundtrips
    details["roundtripFailures"] = failures
    for cls in (opt.Stc15Option, opt.Stc8Option):
        for value in ("high", "low", True, False, "true", "false"):
            state = make(cls, 0)
            state.set_option("rstout_por_state", value)
            expected = "high" if value in ("high", True, "true") else "low"
            getter = next(g for n, g, _ in state.options if n == "rstout_por_state")
            check(getter() == expected, cls.__name__ + " reset level=" + str(value))
        state = make(cls)
        before = state.get_msr()
        rejected(lambda: state.set_option("rstout_por_state", "unrecognized"), cls.__name__ + " rejects unknown level without mutation", lambda: state.get_msr() == before)

    for target in ("STC8H1K08", "STC8H8K64U", "STC32G12K128", "STC32F12K59"):
        model = next(m for m in DB.models if m.name == target)
        for value in ("0", "512", "1024", "65024", str(model.total + 512)):
            device = object.__new__(proto.Stc8dProtocol)
            device.model, device.options = model, make(opt.Stc8Option)
            device.split_code = device.split_eeprom = None
            before = device.options.get_msr()
            if model.mcs251 or not model.iap or int(value) < 512 or int(value) > min(65024, model.total) or int(value) % 512:
                rejected(lambda: device.set_option("program_eeprom_split", value), "S03/S04: reject " + target + " split=" + value,
                         lambda: device.options.get_msr() == before and device.split_code is None and device.split_eeprom is None)
            else:
                device.set_option("program_eeprom_split", value)
                check(device.split_code + device.split_eeprom == model.total and min(device.split_code, device.split_eeprom) >= 0,
                      "S03: valid partition preserves physical capacity: " + target)

    routes = []
    for model in DB.models:
        packet = bytearray(54)
        packet[17] = 0x72
        packet[20:22] = struct.pack(">H", model.magic)
        device = object.__new__(proto.StcAutoProtocol)
        device.status_packet = packet
        device.initialize_model()
        routes.append({"name": model.name, "protocol": device.protocol_name})
    new_families = [item for item in routes if re.match(r"^(STC|IAP|IRC)(12H|15H)", item["name"])]
    check(bool(new_families) and all(item["protocol"] is None for item in new_families),
          "S04: real database STC12H/STC15H entries cannot inherit legacy protocols", new_families)
    check(next(x for x in routes if x["name"] == "IAP15L2K61S2")["protocol"] == "stc15", "qualified IAP15L2K61S2 keeps its existing STC15 route")
    details["modelCount"] = len(routes)
    details["newFamilyModels"] = len(new_families)

    classes = (proto.Stc89Protocol, proto.Stc89AProtocol, proto.Stc12AProtocol, proto.Stc12BProtocol,
               proto.Stc12Protocol, proto.Stc15AProtocol, proto.Stc15Protocol, proto.Stc8Protocol, proto.Stc8dProtocol, proto.Stc8gProtocol)
    malformed = {}
    for cls in classes:
        for length in range(40):
            device = object.__new__(cls)
            device.baud_handshake = 2400
            try:
                device.initialize_status(bytes(length))
            except proto.StcProtocolException:
                pass
            except Exception as error:
                malformed[cls.__name__ + ":" + str(length)] = type(error).__name__
        device = object.__new__(cls)
        device.ser = SimpleNamespace()
        device.initialize_options(bytes(range(64)))
        device.trim_value = b"\x80\x40"
        device.trim_frequency = device.mcu_clock_hz = 24000000
        device.trim_divider, device.bsl_version, device.uid = 1, 0x72, b"FAKEUID"
        device.write_packet = lambda _data: None
        device.read_packet = lambda: b""
        rejected(device.program_options, "S06: controlled empty options acknowledgement: " + cls.__name__)
    check(not malformed, "S06: 400 short status cases produce controlled protocol errors, never IndexError/struct.error", malformed)

    # 原审计冻结的 40 组合法选项应答/报文仍应保持，不能靠禁用旧协议让负例通过。
    wire_packets = []
    for cls in classes:
        for version in (0x65, 0x66, 0x71, 0x72):
            device = object.__new__(cls)
            device.ser = SimpleNamespace()
            device.initialize_options(bytes(range(64)))
            device.trim_value, device.trim_divider, device.uid = b"\x80\x40", 1, b"FAKEUID"
            device.mcu_clock_hz = device.trim_frequency = 24000000
            device.bsl_version = version
            sent = []
            device.write_packet = lambda data: sent.append(bytes(data))
            if cls is proto.Stc89Protocol:
                device.read_packet = lambda: b"\x8d"
            elif cls is proto.Stc89AProtocol:
                device.read_packet = lambda: b"\x04"
            elif cls in (proto.Stc12AProtocol, proto.Stc12BProtocol):
                device.read_packet = lambda: b"\x10" if sent[-1] == b"\x50" else b"\x80"
            elif cls in (proto.Stc12Protocol, proto.Stc15AProtocol):
                device.read_packet = lambda: b"\x50"
            else:
                device.read_packet = lambda: b"\x04\x54"
            device.program_options()
            wire_packets.append({"protocol": cls.__name__, "bslByte": f"{version:02X}", "payloads": [packet.hex().upper() for packet in sent]})
    frozen = json.loads((Path(__file__).parent / "fixtures/option-write-packets.json").read_text(encoding="utf-8"))
    check(wire_packets == frozen, "40 valid options packet plans across ten protocols and four BSL versions remain byte-identical")
    details["validPacketPlans"] = len(wire_packets)

    # 真实前端必须拒绝超容量输入，不能先截断再擦写；有效小程序仍能走完整假传输。
    for size in (1, 1025):
        events = []
        model = DB.MCUModel("OFFLINE-CAPACITY", 0, 1024, 1024, 0, False, True, False)
        device = SimpleNamespace(model=model, split_code=None, split_eeprom=None,
            connect=lambda **_: events.append("connect"), initialize=lambda _: None,
            disconnect=lambda: events.append("disconnect"), handshake=lambda: events.append("handshake"),
            erase_flash=lambda *_: events.append("erase"), program_flash=lambda data: events.append("program:" + str(len(data))),
            program_options=lambda: events.append("options"))
        gal = object.__new__(frontend.StcGal)
        gal.protocol, gal.hexFileType = device, 8
        gal.load_file_auto = lambda _: b"\x00" * size
        gal.opts = SimpleNamespace(version=False, autoreset=False, resetcmd=None, resetpin="dtr", code_image=True, eeprom_image=None, option=[])
        code = gal.run()
        check(code == 0 and events == ["connect", "handshake", "erase", "program:512", "options", "disconnect"] if size == 1
              else code != 0 and events == ["connect", "disconnect"], "capacity frontend: " + str(size) + " bytes", {"exitCode": code, "events": events})

    events = []
    model = next(m for m in DB.models if m.name == "STC32G12K128")
    device = SimpleNamespace(model=model, split_code=None, split_eeprom=None,
        connect=lambda **_: events.append("connect(fake)"), initialize=lambda _: None,
        disconnect=lambda: events.append("disconnect(fake)"), handshake=lambda: events.append("handshake(fake)"),
        erase_flash=lambda *_: events.append("erase(fake)"), program_flash=lambda *_: events.append("program(fake)"),
        program_options=lambda: events.append("options(fake)"))
    gal = object.__new__(frontend.StcGal)
    gal.protocol, gal.hexFileType = device, 8
    gal.load_file_auto = lambda _: b"\x00"
    gal.opts = SimpleNamespace(version=False, autoreset=False, resetcmd=None, resetpin="dtr", code_image=True, eeprom_image=None, option=[])
    exit_code = gal.run()
    check(exit_code != 0 and events == ["connect(fake)", "disconnect(fake)"], "S07: invalid MCS-251 input returns failure without erase/program/options", {"exitCode": exit_code, "events": events})
    if args.guard:
        previous = proto.Stc8dProtocol.set_option
        guard.install_option_safety()
        check(proto.Stc8dProtocol.set_option is previous, "compatibility repairs install once without stacking wrappers")

    if args.status:
        capture = json.loads(Path(args.status).read_text(encoding="utf-8-sig"))
        raw = bytes.fromhex(capture["optionBytesHex"])
        live = []
        for field in ("uart2_pin_mode", "rstout_por_state"):
            state = opt.Stc15Option(raw)
            getter = next(g for n, g, _ in state.options if n == field)
            before_value = getter()
            state.set_option(field, before_value)
            row = {"field": field, "readValue": before_value, "afterValue": getter(), "beforeMsr": raw.hex().upper(), "afterMsr": state.get_msr().hex().upper(), "hardwareWrite": False}
            live.append(row)
            check(state.get_msr() == raw and getter() == before_value, "COM17 actual-option snapshot replay: " + field, row)
        details["hardwareSnapshotReplay"] = live
        details["hardwareCaptureSha256"] = hashlib.sha256(Path(args.status).read_bytes()).hexdigest().upper()

check(not attempted_io, "offline test never opened a serial device", attempted_io)
report = {"passed": all(c["passed"] for c in checks), "mode": "candidate" if args.guard else "baseline", "hardwareIo": False,
          "checks": checks, "details": details, "failures": sum(not c["passed"] for c in checks)}
output = Path(args.output)
output.parent.mkdir(parents=True, exist_ok=True)
output.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
output.with_suffix(".log").write_text(quiet.getvalue(), encoding="utf-8")
print(json.dumps({"passed": report["passed"], "mode": report["mode"], "checks": len(checks), "failures": report["failures"],
                  "roundtrips": roundtrips, "models": len(routes), "hardwareIo": False}))
sys.exit(0 if report["passed"] else 1)
