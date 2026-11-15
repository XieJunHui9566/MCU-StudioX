"""Offline decode of preserved UART bytes and fixed emitted CFG. No hardware IO."""
import collections
import importlib.util
import json
from pathlib import Path
import sys


def require(condition, message):
    if not condition:
        raise ValueError(message)


def trace_path(path, count):
    # 这是冻结负例的控制流核算器，不是生产优化器或通用 ISA 模拟器。
    text = path.read_text(encoding="utf-8")
    text = text[text.index("___m251r4_struct_records:"):]
    labels, instructions = {}, []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith(";"):
            continue
        if line.endswith(":"):
            labels[line[:-1]] = len(instructions)
        elif not line.startswith("."):
            op, *rest = line.split(None, 1)
            instructions.append((op, rest[0] if rest else ""))
    visits, ops = collections.Counter(), collections.Counter()
    pc = init = update = fold = 0
    n = min(count, 64)
    while pc < len(instructions):
        op, arg = instructions[pc]
        visits[pc] += 1
        ops[op] += 1
        require(sum(ops.values()) < 20000, "Unexpected CFG cycle")
        if op == "eret":
            break
        jump = None
        if op == "jle":
            jump = count <= 64
        elif op == "jc":
            require(arg.endswith("_1000002"), "Unknown update branch")
            jump = update < n
        elif op == "jnc":
            if arg.endswith("_1000001"):
                init += 1
                jump = init == 4
            elif arg.endswith("_1000003"):
                fold += 1
                jump = fold == 4
            else:
                raise ValueError("Unknown loop branch")
        elif op in ("ejmp", "sjmp"):
            if arg.endswith("_7"):
                update += 1
            jump = True
        pc = labels[arg] if jump else pc + 1
    require(init == fold == 4 and update == n, "Loop count mismatch")
    start = labels["__wm____m251r4_struct_records_7"]
    end = labels["__wm____m251r4_struct_records_2"]
    blocks = {}
    for name, low, high in (("entry-init", 0, start), ("update-loop", start, end), ("fold-return", end, len(instructions))):
        subset = {index: count for index, count in visits.items() if low <= index < high}
        blocks[name] = dict(instructions=sum(subset.values()),
                            MOV=sum(k for i, k in subset.items() if instructions[i][0] == "mov"),
                            wordSPX=sum(k for i, k in subset.items() if "@spx" in instructions[i][1]))
    return dict(blocks=blocks, instructions=sum(ops.values()), MOV=ops["mov"], PUSH=ops["push"], POP=ops["pop"],
                wordSPX=sum(k for i, k in visits.items() if "@spx" in instructions[i][1]),
                opcodes=dict(ops), loopTrips=[4, n, 4])


def decode(raw):
    records, frames = {}, 0
    for pos in range(len(raw) - 20):
        frame = raw[pos:pos + 21]
        if frame[:2] != b"M9" or frame[-2:] != b"\r\n":
            continue
        checksum = 0
        for value in frame[:19]:
            checksum ^= value
        require(checksum == 0, "Bad UART checksum")
        frames += 1
        data = frame[2:18]
        key = tuple(data[:3])
        record = dict(id=data[0], modeRaw=data[1], sample=data[2], repeats=data[3],
                      ticks=int.from_bytes(data[4:6], "big"), overflow=data[6], status=data[7],
                      result=data[8:12].hex().upper(), remainder=data[12:16].hex().upper())
        require(key not in records or records[key] == record, "Inconsistent duplicate UART record")
        records[key] = record
    return dict(records=list(records.values()), badChecksums=0, frames=frames, uniqueRecords=len(records))


def main():
    require(not sys.flags.optimize, "Run without -O: preserved independent oracle uses assertions")
    root = Path(__file__).resolve().parent
    read = lambda name: json.loads((root / name).read_text(encoding="utf-8-sig"))
    spec = importlib.util.spec_from_file_location("frozen_contract", root / "evidence/mcu_contract.py")
    contract = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(contract)
    case, oracle = read("case.json"), read("evidence/oracle.json")
    for kind in ("parent", "candidate"):
        result = contract.validate_mcu(decode((root / kind / "rx.bin").read_bytes()), oracle, case["measurement"]["ircHz"])
        ticks = next(t["ticks"] for t in result["timings"] if t["name"] == case["function"])
        require(ticks == case["measurement"][kind + "Ticks"], "Measured timing mismatch")
        require(result["passed"] == 1152, "Incomplete correctness evidence")
        for row in read("evidence/path-accounting.json")["rows"]:
            require(trace_path(root / kind / "u6.asm", row["count"]) == row[kind], "Path count mismatch")
    print("PASS: both UART streams, 2304 correctness results, original ticks and 12 frozen CFG paths; no new hardware run")


if __name__ == "__main__":
    main()
