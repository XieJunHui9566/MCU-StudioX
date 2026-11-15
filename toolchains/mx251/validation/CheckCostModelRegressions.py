"""Permanent, offline cost-model negative-case gate. Never opens hardware.

Integrity checking is separate from model qualification. A missing/unsupported
model adapter, or a cost model which still accepts a negative case, fails closed.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parent / "cost-model-regressions"


def read(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def snapshot(root):
    return {p.relative_to(root).as_posix(): sha(p) for p in sorted(root.rglob("*"))
            if p.is_file() and "__pycache__" not in p.parts}


def checked_case(root, entry):
    case = (root / entry["directory"]).resolve()
    if not case.is_relative_to(root.resolve()):
        raise ValueError("Case escapes registry")
    manifest = case / "manifest.json"
    if sha(manifest) != entry["manifestSha256"]:
        raise ValueError("Frozen manifest changed: " + entry["id"])
    pins = read(manifest)["files"]
    actual = snapshot(case)
    actual.pop("manifest.json")
    if actual != pins:
        raise ValueError("Frozen evidence missing, changed, or added: " + entry["id"])
    metadata = read(case / "case.json")
    if metadata["id"] != entry["id"] or metadata.get("requiredDecision") != entry.get("requiredDecision"):
        raise ValueError("Case identity mismatch")
    return case


def validate_response(value, request_sha, entry):
    if value.get("schema") != 1 or value.get("requestSha256") != request_sha:
        raise ValueError("Missing/stale request binding")
    if value.get("caseId") != entry["id"] or value.get("evaluatedBothFrozenAlternatives") is not True:
        raise ValueError("Both original alternatives must actually be evaluated")
    if value.get("selectionBasis") != "cost-model" or not value.get("reason"):
        raise ValueError("Disabling the pass or candidate is not a model rejection")
    if value.get("decision") not in ("accept", "reject"):
        raise ValueError("Missing model decision")
    for name in ("parent", "candidate"):
        cost = value.get("costs", {}).get(name, {})
        if cost.get("known") is not True or not cost.get("metrics"):
            raise ValueError("Missing/unknown full-alternative cost: " + name)
        for metric, amount in cost["metrics"].items():
            if not isinstance(metric, str) or type(amount) not in (int, float) or not 0 <= amount < float("inf"):
                raise ValueError("Invalid numeric cost")
    return value["decision"] == entry["requiredDecision"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--integrity-only", action="store_true")
    parser.add_argument("--model-source", type=Path, help="Complete source directory defining the evaluated model")
    parser.add_argument("--adapter", type=Path, help="Reviewed Python adapter invoking the actual model")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = args.output.resolve()
    roots = [ROOT.resolve()]
    if args.model_source:
        roots.append(args.model_source.resolve())
    if any(output.is_relative_to(p) for p in roots):
        parser.error("Output must be outside frozen fixtures and model sources")
    output.mkdir(parents=True, exist_ok=False)
    report = {"schema": 1, "status": "running", "modelQualified": False,
              "hardwareAccess": False, "runnerSha256": sha(Path(__file__)), "cases": []}
    try:
        registry_path = ROOT / "registry.json"
        registry = read(registry_path)
        report["registrySha256"] = sha(registry_path)
        if registry["schema"] != 1 or not registry["cases"]:
            raise ValueError("Empty/unsupported cost regression registry")
        if (len({e["id"] for e in registry["cases"]}) != len(registry["cases"]) or
                any(e.get("requiredDecision") != "reject" or e.get("permanent") is not True for e in registry["cases"])):
            raise ValueError("Negative cases must be unique, permanent, and require rejection")
        cases = [(entry, checked_case(ROOT, entry)) for entry in registry["cases"]]
        if args.integrity_only:
            report.update(status="integrity-passed-not-model-qualified",
                          cases=[{"id": e["id"], "integrity": "passed"} for e, _ in cases])
            return 0
        if not args.model_source or not args.adapter:
            raise ValueError("Model qualification requires --model-source and --adapter")
        model, adapter = args.model_source.resolve(), args.adapter.resolve()
        before = snapshot(model)
        if not before or not adapter.is_file():
            raise ValueError("Missing model source or adapter")
        report.update(modelSource=str(model), modelFiles=before, adapter=str(adapter), adapterSha256=sha(adapter))
        write(output / "model-inputs.json", before)
        all_passed = True
        for entry, case in cases:
            run = output / entry["id"]
            run.mkdir()
            request = run / "request.json"
            response = run / "response.json"
            write(request, {"schema": 1, "caseId": entry["id"], "caseRoot": str(case),
                            "manifestSha256": entry["manifestSha256"], "modelSource": str(model),
                            "modelFiles": before, "adapterSha256": sha(adapter)})
            command = [sys.executable, "-B", str(adapter), "--request", str(request), "--output", str(response)]
            with (run / "stdout.txt").open("wb") as stdout, (run / "stderr.txt").open("wb") as stderr:
                result = subprocess.run(command, stdout=stdout, stderr=stderr, timeout=180)
            if result.returncode:
                raise ValueError("Model adapter failed/unsupported; inspect " + str(run / "stderr.txt"))
            value = read(response)
            passed = validate_response(value, sha(request), entry)
            report["cases"].append({"id": entry["id"], "passed": passed, "decision": value["decision"],
                                    "requiredDecision": entry["requiredDecision"], "costs": value["costs"],
                                    "responseSha256": sha(response), "command": command})
            all_passed &= passed
        if snapshot(model) != before or sha(adapter) != report["adapterSha256"]:
            raise ValueError("Model/adapter changed during evaluation")
        for entry, _ in cases:
            checked_case(ROOT, entry)
        if sha(registry_path) != report["registrySha256"]:
            raise ValueError("Registry changed during evaluation")
        report.update(status="passed" if all_passed else "failed-model-decision", modelQualified=all_passed)
        return 0 if all_passed else 1
    except Exception as error:
        report.update(status="failed", error=str(error))
        print(str(error), file=sys.stderr)
        return 2
    finally:
        write(output / "result.json", report)
        print(json.dumps({k: report[k] for k in ("status", "modelQualified")}, ensure_ascii=False))


if __name__ == "__main__":
    sys.exit(main())
