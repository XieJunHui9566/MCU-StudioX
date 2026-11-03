"""Offline execution of the official option serializer; no Windows APIs or devices.

The PE is data, never launched. Execution is restricted to the pure serializer,
post-write status mirror and selected label blocks; other branches fail closed.
"""
import hashlib
import json
from pathlib import Path
import struct
import sys
import argparse

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("source", type=Path, help="Existing official AiCube v6.97C PE; read only")
parser.add_argument("output", type=Path, help="New evidence directory")
parser.add_argument("dependencies", type=Path, help="Isolated unicorn 2.1.4 / pefile 2024.8.26 directory")
args = parser.parse_args()
if not __debug__:
    raise RuntimeError("Run without -O; evidence assertions must remain enabled")
HERE = args.output.resolve()
sys.path.insert(0, str(args.dependencies.resolve()))
import pefile
from unicorn import Uc, UC_ARCH_X86, UC_MODE_32, UC_HOOK_CODE
from unicorn.x86_const import UC_X86_REG_ECX, UC_X86_REG_ESP, UC_X86_REG_EAX, UC_X86_REG_EIP, UC_X86_REG_ESI, UC_X86_REG_EBP

EXE = args.source.resolve()
HERE.mkdir(parents=True, exist_ok=False)
SHA = "d857b147f2f4e527c5566a2dd40e63f875c94f4e344fa5ce800ab13a770db420"
assert hashlib.sha256(EXE.read_bytes()).hexdigest() == SHA
pe = pefile.PE(str(EXE))
mapped = pe.get_memory_mapped_image()
base = pe.OPTIONAL_HEADER.ImageBase
assert base == 0x400000
OBJ, CONFIG, OUT, STACK, STOP = 0x2000000, 0x2002000, 0x2003000, 0x3008000, 0x3000000
uc = Uc(UC_ARCH_X86, UC_MODE_32)
uc.mem_map(base, (len(mapped) + 4095) & ~4095)
uc.mem_write(base, mapped)
uc.mem_map(OBJ, 0x10000)
uc.mem_map(0x3000000, 0x10000)

semantic_range = None
labels = []
def check_pc(machine, address, size, _):
    if address == STOP:
        machine.emu_stop()
    elif semantic_range and address == semantic_range[1]:
        machine.emu_stop()
    elif semantic_range and address in (0x43c890, 0x43c8d0):
        sp = machine.reg_read(UC_X86_REG_ESP)
        ret = struct.unpack("<I", machine.mem_read(sp, 4))[0]
        if address == 0x43c890:
            label = struct.unpack("<I", machine.mem_read(sp + 4, 4))[0]
            labels.append(f"{label:04X}")
            machine.reg_write(UC_X86_REG_EAX, label)
        machine.reg_write(UC_X86_REG_ESP, sp + (8 if address == 0x43c890 else 4))
        machine.reg_write(UC_X86_REG_EIP, ret)
    elif semantic_range and semantic_range[0] <= address < semantic_range[1]:
        pass
    elif not 0x44b760 <= address < 0x44bca0:
        raise RuntimeError(f"Execution outside pure serializer: {address:#x}")

uc.hook_add(UC_HOOK_CODE, check_pc)
def w32(address, value):
    uc.mem_write(address, struct.pack("<I", value))

# Exact official F784 table row. Index is derived from the target's name pointer,
# not chosen as an ISA/family shortcut.
name_va = base + mapped.index(b"STC8H8K64U\0")
row = mapped.index(struct.pack("<I", name_va)) + base - 4
index = (row - 0x505468) // 32
assert row == 0x505ae8 and index == 52
assert struct.unpack_from("<H", mapped, row - base + 8)[0] == 0xf784

def encode(config):
    uc.mem_write(OBJ, bytes(0x10000))
    uc.mem_write(CONFIG, bytes(config))
    w32(OBJ + 0x120, index)
    w32(OBJ + 0x6e0, CONFIG)
    w32(OBJ + 0x11c, 24000000)
    w32(OBJ + 0x17c, 0x74)
    uc.mem_write(OBJ + 0x71a, bytes.fromhex("3E2001"))
    w32(STACK, STOP)
    w32(STACK + 4, OUT)
    uc.reg_write(UC_X86_REG_ECX, OBJ)
    uc.reg_write(UC_X86_REG_ESP, STACK)
    uc.emu_start(0x44b760, STOP, count=10000)
    if uc.reg_read(UC_X86_REG_EIP) != STOP:
        raise RuntimeError("Serializer did not return within instruction limit")
    length = uc.reg_read(UC_X86_REG_EAX)
    assert length == 40
    return bytes.fromhex("0400005AA5") + bytes(uc.mem_read(OUT, length))

config = bytearray([0xff] * 20)
config[:6] = bytes.fromhex("BFAFF7FEFF3E")
config[14] = 0xfd
packet = encode(config)
wire = json.loads((Path(__file__).resolve().parent / "StudioX.StcIspValidation/fixtures/stc8h-usb.json").read_text(encoding="utf-8"))
capture = next(f for f in wire if f["rc24MHz"])
expected = bytes.fromhex(capture["tx"][-2])[6:51]
assert packet == expected, (packet.hex(), expected.hex())
vectors = []
for source, destination in [(0, 41), (1, 42), (2, 43), (5, 40), (14, 37)]:
    for value in range(256):
        current = bytearray(config)
        current[source] = value
        result = encode(current)
        # Officially force the internal-clock bit in this mode. It is outside
        # the WDT option masks and must not be inferred to be a WDT setting.
        encoded = value | 0x80 if source == 2 else (value | 0x0c) ^ 2 if source == 14 else value
        changed = bytes(packet[:destination]) + bytes([encoded]) + packet[destination + 1:]
        assert result == changed, (source, destination, value)
        if value in (0, 0x55, 0xaa, 0xff):
            vectors.append({"configOffset": source, "value": value, "commandOffset": destination, "command": result.hex().upper()})
report = {"source": str(EXE), "sha256": SHA, "method": "Unicorn pure x86 serializer; no host API or device access",
          "serializer": "0x44B760", "nativeUsbCaller": "0x44D2E8", "targetTableRow": hex(row),
          "model": "STC8H8K64U", "modelCode": "F784", "bsl": "7.4.12U",
          "baseline": packet.hex().upper(), "baselineCaptureSha256": capture["captureSha256"],
          "rawByteCases": 1280, "hardwareVerified": False, "vectors": vectors}
(HERE / "official-usb-serializer.json").write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({k: v for k, v in report.items() if k != "vectors"}, ensure_ascii=False))

# Selected F784 status-interpretation blocks. Only the language lookup and log
# sinks are stubbed; the machine's bit tests and conditional ID selection run.
blocks = [
    ("reset_pin_enabled", 0x729, 0x10, 0x446ab5, 0x446ad8, ["5242", "5241"]),
    ("clock_gain", 0x728, 2, 0x446998, 0x4469b8, ["5228", "5227"]),
    ("por_reset_delay", 0x728, 0x80, 0x446a8e, 0x446ab5, ["523F", "5240"]),
    ("watchdog_por_enabled", 0x72d, 0x20, 0x446ba0, 0x446bc5, ["522C", "522B"]),
    ("watchdog_stop_idle", 0x72d, 8, 0x446bfe, 0x446c24, ["522F", "522E"]),
    ("low_voltage_reset", 0x729, 0x40, 0x446b50, 0x446b74, ["5247", "5248"]),
    ("eeprom_erase_enabled", 0x727, 2, 0x446c24, 0x446c47, ["5238", "5239"]),
    ("bsl_pindetect_enabled", 0x727, 1, 0x446a6c, 0x446a8e, ["5246", "5245"]),
]
semantic = []
for name, offset, mask, start, end, expected_ids in blocks:
    observed = []
    for value in (0, mask):
        uc.mem_write(OBJ + offset, bytes([value]))
        uc.reg_write(UC_X86_REG_ESI, OBJ)
        uc.reg_write(UC_X86_REG_ESP, STACK)
        semantic_range = (start, end)
        labels.clear()
        uc.emu_start(start, end, count=500)
        observed.extend(labels)
    assert observed == expected_ids, (name, observed)
    semantic.append({"id": name, "reader": hex(start), "clearLabel": observed[0], "setLabel": observed[1]})
semantic_range = None

# Prove which command bytes reach the F784 decoder's normalized status slots.
# C37 goes to S9; C41/C42 are first mirrored to S13/S14, then S10/S11.
# C40 is NOT the EEPROM/download-condition byte, despite a plausible 5-byte tail.
normalization = []
for command_offset, status_offset in ((37, 9), (41, 10), (42, 11), (43, 15), (44, 16)):
    uc.mem_write(OBJ, bytes(0x10000))
    w32(OBJ + 0x120, index)
    for value in range(256):
        selected = bytearray(expected)
        selected[command_offset] = value
        uc.mem_write(OBJ + 0xb1e, bytes(selected))
        uc.reg_write(UC_X86_REG_EBP, OBJ)
        uc.reg_write(UC_X86_REG_ESP, STACK)
        semantic_range = (0x44d445, 0x44d534)
        uc.emu_start(semantic_range[0], semantic_range[1], count=500)
        assert uc.mem_read(OBJ + 0x71e + status_offset, 1)[0] == value
    normalization.append({"commandOffset": command_offset, "normalizedStatusOffset": status_offset})
semantic_range = None

specs = [
    ("reset_pin_enabled", 1, 0x10, {"false": 0, "true": 0x10}),
    ("clock_gain", 0, 2, {"low": 0, "high": 2}),
    ("por_reset_delay", 0, 0x80, {"short": 0, "long": 0x80}),
    ("watchdog_por_enabled", 2, 0x20, {"false": 0x20, "true": 0}),
    ("watchdog_stop_idle", 2, 8, {"false": 8, "true": 0}),
    ("watchdog_prescale", 2, 7, {str(2**(i+1)): i for i in range(8)}),
    ("low_voltage_reset", 1, 0x40, {"false": 0x40, "true": 0}),
    ("low_voltage_threshold", 1, 3, {str(i): 3-i for i in range(4)}),
    ("eeprom_erase_enabled", 14, 2, {"false": 2, "true": 0}),
    ("bsl_pindetect_enabled", 14, 1, {"false": 1, "true": 0}),
]
options = []
for name, offset, mask, values in specs:
    for value, bits in values.items():
        selected = bytearray(config)
        selected[offset] = (selected[offset] & ~mask) | bits
        command = encode(selected)
        options.append({"id": name, "value": value, "command": command.hex().upper()})
fixture = {"sourceSha256": SHA, "evidence": "Official PE serializer and selected status-label code executed offline in Unicorn; not a USB wire capture or hardware acceptance",
           "serializer": "44B760", "usbCaller": "44D2E8", "model": "F784", "rom": "7.4.12U",
           "semantic": semantic, "normalization": normalization, "normalizationCode": "44D445..44D534",
           "baselineCaptureSha256": capture["captureSha256"], "baseline": expected.hex().upper(), "vectors": options}
(HERE / "stc8h-usb-options.json").write_text(json.dumps(fixture, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"PASS {len(blocks)*2} official semantic selections; {len(options)} option packets; no device access")

