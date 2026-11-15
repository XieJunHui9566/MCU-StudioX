"""Historical failing-model replay, NOT a future model evaluator.

The frozen cost diagnostics are reusable only with the exact original model
sources. Changed models must supply an updated production-model adapter.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--request", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    request = json.loads(args.request.read_text(encoding="utf-8"))
    case, model = Path(request["caseRoot"]), Path(request["modelSource"])
    expected = json.loads((case / "historical-model-hashes.json").read_text(encoding="utf-8"))
    for name, digest in expected.items():
        if sha(model / name) != digest:
            raise ValueError("Changed model: historical costs cannot qualify it; implement/review a current-model adapter")
    asm = (case / "candidate/u6.asm").read_text(encoding="utf-8")
    line = next(line for line in asm.splitlines() if line.startswith("; wide local-pointer-copy choice "))
    fields = {key: int(value) for key, value in re.findall(r"([a-z-]+)=(-?\d+)", line)}
    # 从原始生产源码提取选择谓词；只接受此历史接口，不能猜测新模型。
    source = (model / "wide.c").read_text(encoding="utf-8")
    expression = re.search(r"else selected=trialResult==MCS251_WIDE_ACCEPTED && (.*?);", source, re.S).group(1)
    expected_expression = "!oldCost.unknown&&!newCost.unknown&&newCost.bytes<=oldCost.bytes&&newCost.heuristic<=oldCost.heuristic&&newCost.frame<=oldCost.frame&&(newCost.bytes<oldCost.bytes||newCost.heuristic<oldCost.heuristic||newCost.frame<oldCost.frame)"
    if re.sub(r"\s+", "", expression) != expected_expression:
        raise ValueError("Unsupported production selection predicate")
    old = {k: fields["old-" + k] for k in ("bytes", "weighted", "frame", "unknown")}
    new = {k: fields["new-" + k] for k in old}
    selected = not old["unknown"] and not new["unknown"] and all(new[k] <= old[k] for k in ("bytes", "weighted", "frame")) and any(new[k] < old[k] for k in ("bytes", "weighted", "frame"))
    if int(selected) != fields["selected"]:
        raise ValueError("Replay differs from original compiler choice")
    response = {"schema": 1, "requestSha256": sha(args.request), "caseId": request["caseId"],
                "evaluatedBothFrozenAlternatives": True, "selectionBasis": "cost-model",
                "decision": "accept" if selected else "reject", "costs": {
                    key: {"known": not v["unknown"], "metrics": {k: n for k, n in v.items() if k != "unknown"}}
                    for key, v in (("parent", old), ("candidate", new))},
                "reason": "Exact historical model and emitted costs replay; static Pareto gate accepts despite measured slowdown.",
                "limitations": "Replays the frozen original model only. Not recomputation with a changed cost model."}
    args.output.write_text(json.dumps(response, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
