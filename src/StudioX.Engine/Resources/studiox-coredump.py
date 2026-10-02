# MCU StudioX 的有界离线桥接；只使用已锁定的 esp-coredump，不导入用户 Python。
import hashlib
import importlib.metadata
import contextlib
import io
import json
import os
import re
import sys
from esp_coredump import CoreDump
from esp_coredump.corefile.elf import ESPCoreDumpElfFile
from esp_coredump.corefile.loader import ESPCoreDumpFileLoader, EspCoreDumpVersion

target, fmt, dump, app, gdb, metadata = sys.argv[1:]
core_path = dump
if fmt != 'elf':
    loader = ESPCoreDumpFileLoader(dump, fmt == 'b64')
    if loader.target != target:
        raise ValueError(f'Core dump target {loader.target} differs from selected target {target}')
    # 官方加载器校验 CRC/SHA 和 ELF 格式转储内的应用摘要。
    loader.create_corefile(exe_name=app)
    core_path = loader.core_elf_file

core = ESPCoreDumpElfFile(core_path)
embedded = None
chip = None
for segment in core.note_segments:
    for note in segment.note_secs:
        if note.name == b'ESP_CORE_DUMP_INFO' and note.type == ESPCoreDumpElfFile.PT_ESP_INFO:
            chip_id = int.from_bytes(note.desc[:4], 'little') >> 16
            for name in ('ESP32', 'ESP32S2', 'ESP32S3', 'ESP32C3', 'ESP32C2', 'ESP32C6', 'ESP32H2', 'ESP32P4', 'ESP32C5', 'ESP32C61'):
                if getattr(EspCoreDumpVersion, name, None) == chip_id:
                    chip = name.lower()
            if len(note.desc) >= 68:
                candidate = note.desc[4:68].rstrip(b'\x00').decode('ascii')
                if candidate:
                    embedded = candidate
if chip != target:
    raise ValueError(f'Core ELF target {chip} differs from selected target {target}')
if embedded is not None:
    with open(app, 'rb') as f:
        actual = hashlib.sha256(f.read()).hexdigest()
    if not re.fullmatch(r'[0-9a-fA-F]{8,64}', embedded) or not actual.startswith(embedded.lower()):
        raise ValueError(f'Application ELF SHA-256 mismatch: dump={embedded}, selected={actual}')

class OfflineDump(CoreDump):
    def get_gdb_args(self, *args, **kwargs):
        args = super().get_gdb_args(*args, **kwargs)
        # 在加载符号之前禁用所有自动脚本及目标函数调用。
        return args[:1] + ['--nx', '-iex', 'set auto-load off', '-iex', 'set may-call-functions off'] + args[1:]

decoder = OfflineDump(chip=target, core_format='elf', core=core_path, prog=app, gdb=gdb,
                      rom_elf=os.path.join(os.path.dirname(metadata), 'no-rom-selected.elf'), gdb_timeout_sec=10)
capture = io.StringIO()
with contextlib.redirect_stdout(capture):
    decoder.info_corefile()
text = capture.getvalue()
if len(text) > 2 * 1024 * 1024:
    raise ValueError('CoreDump output exceeds 2 MiB')
print(text)
tasks, _ = decoder.get_task_info_extra_note_tuple()
task_rows = []
for task in tasks or []:
    task_rows.append(dict(name=task.task_name.split(b'\0')[0].decode('utf-8', 'replace'), tcb=task.task_tcb_addr,
                          stackStart=task.task_stack_start, stackBytes=task.task_stack_len, flags=task.task_flags))
panic = decoder.get_panic_details()
if not task_rows:
    # 老转储没有 ESP_TASK_INFO note；只记录 GDB 实际解出的字段，不猜测栈起点或损坏标志。
    for match in re.finditer(r'^\s*(0x[0-9a-fA-F]+)\s+(\S+)\s+\d+/\d+\s+(\d+)/(\d+)\s*$', text, re.MULTILINE):
        task_rows.append(dict(name=match[2], tcb=int(match[1], 16), stackStart=None,
                              stackBytes=int(match[3])+int(match[4]), flags=None))
crashed = re.search(r"Crashed task handle: 0x[0-9a-fA-F]+, name: '([^']+)'", text)
with open(metadata, 'w', encoding='utf-8') as f:
    json.dump(dict(target=target, decoderVersion=importlib.metadata.version('esp-coredump'), embeddedElfHash=embedded,
                   tasks=task_rows, crashedTask=crashed[1] if crashed else None,
                   panicDetails=panic.desc.rstrip(b'\0').decode('utf-8', 'replace') if panic else None), f)
