"""Replay frozen startup-root regression assets without compiler or hardware.

The byte decoder intentionally accepts the rejected image's valid relocation
structure; a separate startup dependency contract must still reject that image.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys

sys.dont_write_bytecode = True
import relocation

ROOT = Path(__file__).resolve().parent


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def pin(path):
    return {"path": str(Path(path).resolve()), "sha256": sha(path)}


def save(path, value):
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2)+"\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, help="New writable result directory outside the frozen assets")
    args = parser.parse_args()
    out = Path(args.output).resolve()
    if out == ROOT or ROOT in out.parents:
        raise ValueError("Runtime output must be outside the permanent assets")
    if out.exists():
        raise ValueError("Use a new output directory; do not overwrite a previous run")
    manifest = json.loads((ROOT / "manifest.json").read_text(encoding="utf-8"))
    files = {}
    for item in manifest["files"]:
        path = (ROOT / item["path"]).resolve()
        path.relative_to(ROOT)
        if sha(path) != item["sha256"] or path.stat().st_size != item["bytes"]:
            raise ValueError("Frozen file changed: " + item["path"])
        files[item["path"]] = path
    out.mkdir(parents=True)
    contract, rows = manifest["contract"], []
    for case in manifest["cases"]:
        directory = out / case["target"] / case["name"]
        directory.mkdir(parents=True)
        objects = case["directObjects"] + [x["path"] for x in case["members"]]
        session = {"schema": "mx251.permanent-startup-replay-session.v1", "link": {
            "direct_objects": [pin(files[name]) for name in objects], "archives": [], "selected_members": [],
            "map": pin(files[case["map"]]), "image": pin(files[case["image"]])}}
        session_path = directory / "session.json"
        save(session_path, session)
        proof = relocation.run(session_path)
        save(directory / "machine-proof.json", proof)
        if proof["status"] != "exact-linked-byte-match":
            raise ValueError("Machine proof did not cover the final image")
        asm_path = next(files[x] for x in case["assemblies"] if Path(x).name == "u0.asm")
        assembly = asm_path.read_text(encoding="utf-8-sig")
        extracted = {x["name"]: x for x in case["members"]}
        object_bytes = {str(Path(x["name"]).resolve()): x["bytes"] for x in proof["objects"]}
        issues = []
        for symbol in contract["startupSymbols"]:
            if not re.search(r"^\s*\.globl\s+"+re.escape(symbol)+r"\s*$", assembly, re.M):
                issues.append({"code": "REQUIRED_FORCE_LINK_DECLARATION_REMOVED", "symbol": symbol})
            if symbol not in proof["symbols"]:
                issues.append({"code": "REQUIRED_STARTUP_SYMBOL_ABSENT", "symbol": symbol})
        crt = {}
        for name in contract["startupArchiveMembers"]:
            member = extracted.get(name)
            count = object_bytes.get(str(files[member["path"]])) if member else None
            if not member or not count:
                issues.append({"code": "REQUIRED_STARTUP_ARCHIVE_NOT_EXTRACTED", "member": name})
            else:
                crt[name] = {"sha256": sha(files[member["path"]]), "codeBytes": count}
        status = "REJECT" if issues else "PASS"
        row = {"target": case["target"], "case": case["name"], "expected": case["expectedStartup"],
               "actualStartup": status, "checkPassed": status == case["expectedStartup"], "issues": issues,
               "payloadBytes": proof["byte_count"], "requiredCrtMembers": crt,
               "machineProof": pin(directory / "machine-proof.json"), "image": pin(files[case["image"]]),
               "runtimeExecuted": False}
        if case["initializedProbe"]:
            memory = relocation.read_hex(files[case["image"]].read_text(encoding="ascii"))
            initialization = []
            for area in proof["placements"]:
                if area["name"] in {"XINIT", "CONST"} and area["size"]:
                    initialization.append({"area": area["name"], "hex": bytes(memory[area["base"]+i] for i in range(area["size"])).hex()})
            row["initialization"] = initialization
            row["dataSymbols"] = {name: proof["symbols"][name] for name in contract["dataSymbols"]}
            if not any(x["area"] == "XINIT" and len(x["hex"]) >= 18 for x in initialization):
                raise ValueError("Actual nonzero XDATA initializer is missing")
            if not any(x["area"] == "CONST" and "10325476" in x["hex"] for x in initialization):
                raise ValueError("Actual CODE copy source is missing")
        rows.append(row)
        save(directory / "result.json", row)
    pairs = []
    for target in {x["target"] for x in rows}:
        by_name = {x["case"]: x for x in rows if x["target"] == target}
        parent, candidate = by_name["initialized-parent"], by_name["initialized-safe-edf"]
        for key in ("requiredCrtMembers", "initialization", "dataSymbols"):
            if parent[key] != candidate[key]:
                raise ValueError("GC changed actual initialization contract: " + target + ": " + key)
        if by_name["parent"]["requiredCrtMembers"] != by_name["safe-edf"]["requiredCrtMembers"]:
            raise ValueError("Safe same-source candidate changed the required CRT extraction")
        pairs.append({"target": target, "safeInitializationPreserved": True,
                      "rejectedLostBytes": by_name["safe-edf"]["payloadBytes"] - by_name["rejected-35f"]["payloadBytes"]})
    result = {"schema": "mx251.permanent-startup-link-roots.result.v1", "status": "PASS" if all(x["checkPassed"] for x in rows) else "FAIL",
              "manifest": pin(ROOT / "manifest.json"), "checker": pin(__file__), "decoder": pin(ROOT / "relocation.py"),
              "cases": rows, "pairedInitialization": pairs, "frozenFilesChecked": len(files), "hardwareExecuted": False,
              "scope": "Actual REL/MAP/HEX byte reconstruction plus forced startup dependencies and initializer bytes; does not execute firmware or prove runtime/ISR/cycle behavior"}
    save(out / "results.json", result)
    print(json.dumps({"status": result["status"], "cases": len(rows), "rejectedAsExpected": sum(x["actualStartup"] == "REJECT" and x["checkPassed"] for x in rows), "output": str(out / "results.json")}))
    return 0 if result["status"] == "PASS" else 1


if __name__ == "__main__":
    raise SystemExit(main())
