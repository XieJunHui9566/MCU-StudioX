"""Test gate rejection semantics, without compiling firmware or accessing hardware."""
import copy
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from CheckCostModelRegressions import ROOT, checked_case, read, sha, validate_response


class GateChecks(unittest.TestCase):
    def setUp(self):
        self.entry = read(ROOT / "registry.json")["cases"][0]
        self.value = {"schema": 1, "requestSha256": "current-request", "caseId": self.entry["id"],
                      "evaluatedBothFrozenAlternatives": True, "selectionBasis": "cost-model",
                      "decision": "reject", "reason": "Synthetic gate contract test only; not model qualification",
                      "costs": {"parent": {"known": True, "metrics": {"cost": 10}},
                                "candidate": {"known": True, "metrics": {"cost": 11}}}}

    def test_rejection_contract_and_acceptance_failure(self):
        self.assertTrue(validate_response(self.value, "current-request", self.entry))
        self.value["decision"] = "accept"
        self.assertFalse(validate_response(self.value, "current-request", self.entry))

    def test_missing_stale_disabled_unknown_are_not_passes(self):
        changes = [lambda v: v.pop("requestSha256"), lambda v: v.update(requestSha256="old-request"),
                   lambda v: v.update(caseId="different-pair"),
                   lambda v: v.update(evaluatedBothFrozenAlternatives=False),
                   lambda v: v.update(selectionBasis="pass-disabled"), lambda v: v.update(reason=""),
                   lambda v: v.update(decision="unknown"),
                   lambda v: v["costs"].pop("candidate"),
                   lambda v: v["costs"]["candidate"].update(known=False),
                   lambda v: v["costs"]["parent"].update(metrics={}),
                   lambda v: v["costs"]["candidate"]["metrics"].update(cost=float("nan")),
                   lambda v: v["costs"]["candidate"]["metrics"].update(cost=-1)]
        for index, mutate in enumerate(changes):
            with self.subTest(index=index):
                value = copy.deepcopy(self.value)
                mutate(value)
                with self.assertRaises(ValueError):
                    validate_response(value, "current-request", self.entry)

    def test_manifest_missing_changed_and_added(self):
        # 小型临时清单用于故障注入，绝不修改永久冻结输入。
        from CheckCostModelRegressions import write
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            case = root / "case"
            case.mkdir()
            write(case / "case.json", {"id": "synthetic", "requiredDecision": "reject"})
            write(case / "manifest.json", {"files": {"case.json": sha(case / "case.json")}})
            entry = {"directory": "case", "id": "synthetic", "requiredDecision": "reject", "manifestSha256": sha(case / "manifest.json")}
            checked_case(root, entry)
            original = (case / "case.json").read_bytes()
            for mode in ("missing", "changed", "added"):
                if mode == "missing":
                    (case / "case.json").unlink()
                elif mode == "changed":
                    (case / "case.json").write_bytes(b"{}")
                else:
                    (case / "extra.txt").write_text("extra")
                with self.subTest(mode=mode), self.assertRaises(ValueError):
                    checked_case(root, entry)
                (case / "case.json").write_bytes(original)
            entry["manifestSha256"] = "0" * 64
            with self.assertRaises(ValueError):
                checked_case(root, entry)

    def test_actual_historical_adapter_still_fails(self):
        case = checked_case(ROOT, self.entry)
        runner = Path(__file__).with_name("CheckCostModelRegressions.py")
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "replay"
            result = subprocess.run([sys.executable, "-B", runner, "--model-source", case / "historical-model",
                                     "--adapter", ROOT / "ReplayV0136.py", "--output", output], capture_output=True)
            self.assertEqual(result.returncode, 1, result.stderr.decode(errors="replace"))
            report = read(output / "result.json")
            self.assertEqual(report["status"], "failed-model-decision")
            self.assertFalse(report["modelQualified"])
            self.assertEqual(report["cases"][0]["decision"], "accept")
            # 伪造的新源码指纹不得允许重用历史成本。
            model = Path(directory) / "changed-model"
            model.mkdir()
            for path in (case / "historical-model").iterdir():
                (model / path.name).write_bytes(path.read_bytes())
            with (model / "wide_cost.h").open("a") as file:
                file.write("\n/* changed model */\n")
            changed_output = Path(directory) / "changed-replay"
            result = subprocess.run([sys.executable, "-B", runner, "--model-source", model,
                                     "--adapter", ROOT / "ReplayV0136.py", "--output", changed_output], capture_output=True)
            self.assertEqual(result.returncode, 2)
            self.assertFalse(read(changed_output / "result.json")["modelQualified"])

    def test_acceptance_cannot_omit_model_inputs_when_change_declared(self):
        acceptance = Path(__file__).with_name("RunAcceptance.py")
        required = [part for name in ("profiles", "toolchain", "cmake", "ninja", "pwsh", "output")
                    for part in ("--" + name, "intentionally-unused")]
        for extra in (["--cost-model-change"], ["--cost-model-source", "intentionally-unused"],
                      ["--cost-model-adapter", "intentionally-unused"]):
            with self.subTest(extra=extra):
                process = subprocess.run([sys.executable, "-B", acceptance, *required, *extra], capture_output=True)
                self.assertEqual(process.returncode, 2)
                self.assertIn(b"Cost-model changes require both", process.stderr)


if __name__ == "__main__":
    unittest.main()
