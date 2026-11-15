"""Compile and execute the production loop guard on both frozen alternatives.

MX251_HOST_CC selects a private host C compiler; no compiler is downloaded.
This adapter checks the loop guard only. Its rejection is a sufficient veto in
the current conjunctive production selector, not a prediction of chip cycles.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    request = json.loads(args.request.read_text(encoding="utf-8"))
    case, model, work = Path(request["caseRoot"]), Path(request["modelSource"]), args.output.parent
    compiler = shutil.which(os.environ.get("MX251_HOST_CC", "gcc"))
    if not compiler:
        raise ValueError("Set MX251_HOST_CC to the existing host C compiler")
    compiler = Path(compiler)
    definition = json.loads((case / "case.json").read_text(encoding="utf-8"))
    inputs = []
    for kind in ("parent", "candidate"):
        assembly = (case / kind / "u6.asm").read_text(encoding="utf-8")
        label = "___m251r4_" + definition["function"] + ":"
        body = assembly[assembly.index(label):]
        body = body[:body.index("\t.area")]
        target = work / (kind + ".asm")
        target.write_text(body, encoding="utf-8")
        inputs.append(target)
    exe = work / ("probe.exe" if os.name == "nt" else "probe")
    command = [str(compiler), "-std=c17", "-O2", "-Wall", "-I", str(model),
               str(model / "tests/probe-wide-loop-cost.c"), "-o", str(exe)]
    environment = os.environ.copy()
    for key in ("CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "LIBRARY_PATH", "COMPILER_PATH", "GCC_EXEC_PREFIX"):
        environment.pop(key, None)
    result = subprocess.run(command, capture_output=True, timeout=120, env=environment)
    (work / "compile.stdout.txt").write_bytes(result.stdout)
    (work / "compile.stderr.txt").write_bytes(result.stderr)
    if result.returncode:
        raise ValueError("Production loop-cost probe did not compile")
    result = subprocess.run([str(exe), *map(str, inputs)], capture_output=True, timeout=30)
    (work / "probe.stdout.txt").write_bytes(result.stdout)
    (work / "probe.stderr.txt").write_bytes(result.stderr)
    if result.returncode:
        raise ValueError("Production loop-cost probe failed")
    actual = json.loads(result.stdout)
    names = ("instructions", "MOV", "memoryOps", "stackReads", "stackWrites", "PUSH", "POP")
    costs = {}
    for kind in ("parent", "candidate"):
        costs[kind] = {"known": not actual[kind]["unknown"], "metrics": {"loopCount": len(actual[kind]["loops"])}}
        for index, loop in enumerate(actual[kind]["loops"]):
            costs[kind]["metrics"].update({f"loop{index}.{name}": value for name, value in zip(names, loop["metrics"])})
        # Newer production probes expose every aligned branch path. Preserve these
        # costs instead of presenting per-loop maxima as the selection criterion.
        for path in actual[kind].get("paths", []):
            prefix = f"path.{path['loop']}.{path['seen']}.{path['taken']}.{path['exited']}"
            costs[kind]["metrics"].update({prefix + "." + name: value for name, value in zip(names, path["metrics"])})
    response = {"schema": 1, "requestSha256": sha(args.request), "caseId": request["caseId"],
                "evaluatedBothFrozenAlternatives": True, "selectionBasis": "cost-model", "costs": costs,
                "decision": "unknown" if actual["decision"] < 0 else "accept" if actual["decision"] else "reject",
                "reason": "Production loop-cost veto: compare aligned iteration paths independently; no cold-region or cross-path compensation. Older probes cover only single-path cycles.",
                "actual": actual, "compilerSha256": sha(compiler), "compiler": str(compiler),
                "probeSha256": sha(exe), "command": command,
                "limitations": "Instruction/access counts, not cycles. Coverage and bounds are defined by the bound production source; unknown or unmatched paths cannot qualify."}
    args.output.write_text(json.dumps(response, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
