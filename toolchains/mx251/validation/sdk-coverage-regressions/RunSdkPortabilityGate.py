#!/usr/bin/env python3
"""Read-only SDK portability evidence gate. Never builds, changes goldens or accesses a device."""
import argparse
import hashlib
import json
import re
from pathlib import Path

SCHEMA = "mx251.sdk-portability-manifest.v1"
FEATURES = {"cross-tu", "isr", "data-pointer", "explicit-root", "shared-tail",
            "opaque-fallback", "indirect-fallback", "missing-metadata-fallback",
            "case-sensitive", "startup-initialization", "machine-bytes"}
GLOBAL_ROLES = {"cost-negative", "cost-positive", "gc-properties", "capacity",
                "source-binding", "fallback-census", "sdk-inputs"}


class Invalid(Exception):
    pass


def require(condition, message):
    if not condition:
        raise Invalid(message)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def pointer(document, expression):
    require(isinstance(expression, str) and (not expression or expression.startswith("/")), "Invalid JSON pointer")
    node = document
    for piece in expression.split("/")[1:] if expression else []:
        piece = piece.replace("~1", "/").replace("~0", "~")
        node = node[int(piece)] if isinstance(node, list) else node[piece]
    return node


def equal(left, right):
    # Python's True == 1 must not turn a numeric/boolean assertion into a pass.
    return type(left) is type(right) and left == right


class Gate:
    def __init__(self, manifest, base):
        self.m = manifest
        self.base = base
        self.documents = {}
        self.files = {}
        self.errors = []
        self.pending = []
        self.checks = []
        self.coverage = {}

    def pin(self, item):
        require(isinstance(item, dict), "Expected explicit path/SHA-256 pin")
        raw, digest = item.get("path"), item.get("sha256")
        require(isinstance(raw, str) and bool(raw), "Missing pinned path")
        require(isinstance(digest, str) and re.fullmatch(r"[0-9a-fA-F]{64}", digest), "Missing or invalid SHA-256")
        path = Path(raw)
        path = (path if path.is_absolute() else self.base / path).resolve()
        require(path.is_file(), f"Missing evidence file: {path}")
        actual = self.files.get(str(path))
        if actual is None:
            actual = sha(path)
            self.files[str(path)] = actual
        require(actual == digest.lower(), f"Hash mismatch: {path}")
        return path

    def read(self, item):
        path = self.pin(item)
        if str(path) not in self.documents:
            self.documents[str(path)] = json.loads(path.read_text(encoding="utf-8-sig"))
        return self.documents[str(path)]

    def evidence(self, name):
        require(name in self.m["evidence"], f"Missing named evidence: {name}")
        return self.read(self.m["evidence"][name])

    def value(self, ref):
        require(isinstance(ref, dict) and set(ref) == {"evidence", "pointer"}, "Expected evidence/pointer reference")
        return pointer(self.evidence(ref["evidence"]), ref["pointer"])

    def assert_value(self, assertion):
        actual = self.value(assertion["at"])
        operation = assertion.get("op", "equals")
        if operation == "equals":
            require("value" in assertion and assertion["value"] is not None, "Unknown/null cannot prove a claim")
            require(equal(actual, assertion["value"]), f"Assertion mismatch: {assertion['at']}")
        elif operation == "nonempty":
            require(isinstance(actual, (str, list, dict)) and len(actual) > 0, f"Empty evidence: {assertion['at']}")
        elif operation == "at-least":
            require(type(actual) is int and type(assertion.get("value")) is int and actual >= assertion["value"], "Count assertion failed")
        else:
            raise Invalid(f"Unsupported assertion operation: {operation}")

    def claim(self, claim):
        role = claim["role"]
        require(role in GLOBAL_ROLES | {"profile-feature"}, f"Unsupported claim role: {role}")
        require(claim.get("assertions"), "Evidence claim has no executable assertion")
        for assertion in claim["assertions"]:
            self.assert_value(assertion)
        document = self.value(claim["document"])
        if role == "cost-negative":
            require(document.get("status") == "passed" and document.get("modelQualified") is True, "Production cost negative not qualified")
            require(document.get("cases"), "No frozen cost negative case")
            for case in document["cases"]:
                require(case.get("passed") is True and case.get("decision") == "reject", "Frozen negative was not explicitly rejected")
                require(all(case.get("costs", {}).get(side, {}).get("known") is True for side in ["parent", "candidate"]), "Unknown cost cannot qualify")
        elif role == "cost-positive":
            require(document.get("status") == "PASS" and document.get("decision", {}).get("decision") == 1, "Actual production model did not accept positive")
            require(all(document["decision"][side].get("unknown") == 0 for side in ["parent", "candidate"]), "Positive has unknown costs")
            for name, digest in document["modelPins"].items():
                require(self.files.get(str((Path(self.m["modelSourceRoot"]) / name).resolve())) == digest, "Positive model source not bound to pinned current source")
        elif role == "source-binding":
            require(document.get("decision") == "reject" and document.get("modelSourcePins"), "Missing actual model source identity")
            require(Path(document["modelSource"]).resolve() == Path(self.m["modelSourceRoot"]).resolve(), "Wrong model source directory")
            for name, digest in document["modelSourcePins"].items():
                self.pin({"path": str(Path(document["modelSource"]) / name), "sha256": digest})
            self.read(document["originalNegativeGate"])
        elif role == "gc-properties":
            require(document.get("passed") is True and document.get("engineUnchanged") is True, "Current engine properties failed")
            replay = document.get("startupCounterexampleReplay", {})
            require(replay.get("status") == "PASS" and replay.get("exitCode") == 0, "Startup negative replay missing")
            self.read({"path": replay["results"], "sha256": replay["resultsSha256"]})
            for test in document["tests"]:
                require(test.get("exitCode") == 0, "Engine property test failed")
                self.read({"path": test["results"], "sha256": test["resultsSha256"]})
        elif role == "capacity":
            require(document.get("passed") is True and not document.get("failed"), "Capacity checks not qualified")
            events = self.value(claim["events"])
            latest = {event["name"]: event for event in events["results"]}
            require(len(latest) == document["testCount"] and all(event.get("passed") is True for event in latest.values()), "Capacity history/summary mismatch")
            required_capacity = {"value-boundaries", "node-boundaries", "high-bitset", "alias-interference", "invalid-options", "atomic-fallback", "private-abi-rejection"}
            require(set(claim.get("capacityFeatures", {})) == required_capacity, "Capacity feature coverage incomplete")
            for names in claim["capacityFeatures"].values():
                require(names and all(name in latest for name in names), "Capacity coverage maps to absent tests")
        elif role == "fallback-census":
            require(document.get("status") == "complete" and document.get("auditIssues") == [], "Fallback census incomplete")
        elif role == "sdk-inputs":
            require(document.get("status") == "offline-replay-complete" and document.get("sdkAndParentPinsUnchanged") is True, "SDK source identity is not frozen")
        identities = set()
        for binding in claim.get("bindings", []):
            name = binding["identity"]
            require(name in self.m["identities"], f"Unknown identity: {name}")
            require(self.value(binding["at"]) == self.m["identities"][name]["sha256"], f"Not current {name}: {binding['at']}")
            identities.add(name)
        required = {"compiler"} if role == "capacity" else {"engine"} if role == "gc-properties" else set()
        if role == "profile-feature":
            required = {"compiler", "engine"}
            profile = claim["profile"]
            require(profile in self.coverage, f"Undeclared profile: {profile}")
            require(self.value(claim["profileAt"]) == profile, "Profile must be present in actual result, not just manifest")
            features = set(claim.get("features", []))
            require(features and features <= FEATURES, "Unknown/empty profile feature set")
            require(claim.get("machineProofs"), "Real profile claim requires final-byte proofs")
            for proof_ref in claim["machineProofs"]:
                proof = self.read(self.value(proof_ref))
                require(proof.get("status") == "exact-linked-byte-match", "Unsupported/incomplete final-byte proof")
                require(type(proof.get("byte_count")) is int and proof["byte_count"] > 0, "Empty machine-byte proof")
            self.coverage[profile].update(features)
        require(required <= identities, f"Missing current identity binding for {role}: {required - identities}")
        return role

    def evaluate(self):
        require(self.m.get("schema") == SCHEMA, "Unsupported manifest schema")
        require(set(self.m.get("identities", {})) >= {"compiler", "engine"}, "Missing candidate identities")
        for value in self.m["identities"].values():
            self.pin(value)
        require(self.m.get("sourcePins"), "No candidate production source pins")
        for value in self.m["sourcePins"]:
            self.pin(value)
        profiles = self.m.get("profiles", [])
        ids = [profile["id"] for profile in profiles]
        require(len(ids) >= 2 and len(ids) == len(set(ids)), "At least two distinct explicit Target Profiles required")
        for profile in profiles:
            lock = self.read(profile["lock"])
            require(lock.get("target") == profile["id"], "Profile lock target mismatch")
            require(profile.get("capabilityEvidence"), "Missing ABI/address-space/startup capability contract")
            for pin in profile["capabilityEvidence"]:
                self.pin(pin)
            self.coverage[profile["id"]] = set()
        require(self.m.get("evidence"), "Empty evidence set")
        for name in self.m["evidence"]:
            self.evidence(name)
        # Explicit dependencies are normalized by the producer. No implicit directory discovery,
        # arbitrary code evaluation or trust in a filename that happens to contain 'PASS'.
        for pin in self.m.get("dependencyPins", []):
            self.pin(pin)
        roles = set()
        for claim in self.m.get("claims", []):
            try:
                role = self.claim(claim)
                roles.add(role)
                self.checks.append({"id": claim["id"], "status": "PASS", "role": role})
            except (Invalid, KeyError, IndexError, ValueError, TypeError) as error:
                self.errors.append(f"Claim {claim.get('id')}: {error}")
        for missing in sorted(GLOBAL_ROLES - roles):
            self.pending.append(f"Missing qualified global role: {missing}")
        for target, present in self.coverage.items():
            missing = sorted(FEATURES - present)
            if missing:
                self.pending.append({"profile": target, "missingRealFeatures": missing})
        for item in self.m.get("pendingEvidence", []):
            require(item.get("reason"), "Pending evidence must state exact reason")
            self.pending.append(item)
        projects = self.projects()
        require(self.m.get("environment", {}).get("receiptStatus") in {"captured", "external-manifest-only"}, "Capacity environment identity not described")
        if self.m["environment"]["receiptStatus"] == "external-manifest-only":
            require(self.m["environment"].get("debt"), "Missing receipt limitation explanation")
        offline = "FAIL" if self.errors else "PENDING" if self.pending else "PASS"
        performance = "FAIL_SIZE" if any(not row["size110Pass"] for row in projects) else "PENDING_TIME"
        return {"schema": "mx251.sdk-portability-gate.result.v1", "offlineEvidenceStatus": offline,
                "offlineEvidencePass": offline == "PASS", "performanceStatus": performance,
                "hardwareStatus": "PENDING_NOT_QUALIFIED_BY_OFFLINE_GATE", "hardwareQualified": False,
                "overallQualified": False, "errors": self.errors, "pending": self.pending,
                "checks": self.checks, "profileCoverage": {k: sorted(v) for k, v in self.coverage.items()},
                "projects": projects, "checkedDistinctFiles": len(self.files),
                "environment": self.m["environment"],
                "limitations": ["Checks authenticated existing checker outputs and explicit semantic mappings; does not independently execute target semantics.",
                                "v1 is an offline migration gate. Runtime/time claims require a separate board-specific validator; never promoted from static or ACK evidence.",
                                "PASS only qualifies the explicit manifest scope, not every SDK/Profile or absence of all future bugs."]}

    def projects(self):
        expected = self.m.get("expectedProjects", [])
        records = self.m.get("projects", [])
        require(expected and len(expected) == len(set(expected)), "Expected full project scope must be explicit and unique")
        require(sorted(row["id"] for row in records) == sorted(expected), "Project omissions/duplicates in complete SDK matrix")
        output = []
        for item in records:
            build = self.value(item["buildRow"])
            require(build.get("status") == "offline-build-and-evidence-pass", "Full project build is not qualified")
            require(build.get("sourceOrderDefinesUnchanged") is True, "Frozen source/order/macros not proved unchanged")
            require(build.get("functionGc", {}).get("scriptSha256") == self.m["identities"]["engine"]["sha256"], "SDK build engine differs")
            firmware = build["firmware"]
            self.pin(firmware)
            require(type(build.get("payloadBytes")) is int and build["payloadBytes"] > 0, "Unknown SDK occupied bytes")
            require(build["payloadBytes"] == firmware["payloadBytes"], "SDK image/size disagreement")
            receipt = self.read(self.value(item["receipt"]))
            # Explicit receipt selection keeps old schema variations outside executable policy.
            require(pointer(receipt, item["receiptCompilerPointer"]) == self.m["identities"]["compiler"]["sha256"], "SDK receipt compiler differs")
            require(pointer(receipt, item["receiptProfilePointer"]) == item["profile"], "SDK receipt profile differs")
            require(item["profile"] in self.coverage, "SDK profile not declared")
            proof = self.value(item["proofRow"])
            require(proof.get("status") == "PASS_EXACT_MACHINE_BYTES", "SDK final bytes not proved")
            require(proof["image"]["sha256"] == firmware["sha256"], "SDK byte proof belongs to a different image")
            startup = self.value(item["startupRow"])
            require(startup["firmware"]["sha256"] == firmware["sha256"], "SDK startup proof belongs to a different image")
            require(startup.get("preservedStartupArchiveMembers") and startup.get("preservedCompilerForceLinkDeclarations"), "Startup imports/archive extraction not proved")
            self.read(startup["gcManifest"])
            self.pin(startup["linkerMap"])
            census = self.value(item["censusRow"])
            require(census.get("compilerSha256") == self.m["identities"]["compiler"]["sha256"], "Coverage census compiler differs")
            require(census["image"]["sha256"] == firmware["sha256"], "Coverage census belongs to a different image")
            refusal_count = 0
            for function in census.get("functions", {}).values():
                for refusal in function.get("metrics", {}).get("wideRefusal", []):
                    require(all(str(refusal.get(k, "")) for k in ["function", "stage", "reason", "icode"]), "Unobservable whole-function fallback")
                    refusal_count += 1
            require(type(build.get("keilSizePayloadBytes")) is int and build["keilSizePayloadBytes"] > 0, "No comparable size denominator")
            settings = build.get("keilSettings", {})
            require(all(k in settings for k in ["compilerVersion", "optimizationLevel", "optimization", "memoryModel", "romModel"]), "Reference optimization/configuration omitted")
            size_pass = build["payloadBytes"] * 10 <= build["keilSizePayloadBytes"] * 11
            require(item.get("time", {}).get("state") in {"NOT_MEASURED", "NO_FINITE_WINDOW", "PENDING_VALIDATOR"}, "v1 cannot accept unvalidated target timing claims")
            require(item["time"].get("reason"), "Missing exact timing limitation")
            require(item.get("correctness", {}).get("state") in {"NOT_MEASURED", "PENDING_VALIDATOR"}, "v1 cannot accept unvalidated hardware correctness claims")
            require(item["correctness"].get("reason"), "Missing runtime correctness limitation")
            output.append({"id": item["id"], "profile": item["profile"], "bytes": build["payloadBytes"],
                           "referenceBytes": build["keilSizePayloadBytes"], "size110Pass": size_pass,
                           "sizePercent": 100 * build["payloadBytes"] / build["keilSizePayloadBytes"],
                           "referenceSettings": settings, "wholeFunctionRefusals": refusal_count,
                           "time": item["time"], "correctness": item["correctness"]})
        return output


def run(manifest_path):
    path = Path(manifest_path).resolve()
    manifest = json.loads(path.read_text(encoding="utf-8-sig"))
    gate = Gate(manifest, path.parent)
    try:
        result = gate.evaluate()
    except (Invalid, KeyError, IndexError, ValueError, TypeError, OSError) as error:
        result = {"schema": "mx251.sdk-portability-gate.result.v1", "offlineEvidenceStatus": "FAIL",
                  "offlineEvidencePass": False, "hardwareQualified": False, "overallQualified": False,
                  "errors": gate.errors + [str(error)], "pending": gate.pending,
                  "checkedDistinctFiles": len(gate.files)}
    result["manifest"] = {"path": str(path), "sha256": sha(path)}
    result["checker"] = {"path": str(Path(__file__).resolve()), "sha256": sha(__file__)}
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True)
    parser.add_argument("--output", required=True, help="Fresh result JSON; existing file is never overwritten")
    args = parser.parse_args()
    output = Path(args.output).resolve()
    require(not output.exists(), "Refusing to overwrite evidence")
    result = run(args.manifest)
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False, indent=2)
        handle.write("\n")
    print(json.dumps({key: result.get(key) for key in ["offlineEvidenceStatus", "performanceStatus", "hardwareQualified", "checkedDistinctFiles", "errors", "pending"]}, ensure_ascii=False))
    return 0 if result["offlineEvidenceStatus"] == "PASS" else 2 if result["offlineEvidenceStatus"] == "PENDING" else 1


if __name__ == "__main__":
    raise SystemExit(main())
