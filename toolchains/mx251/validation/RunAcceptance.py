"""Offline fixed-project qualification. CMake owns sources; Ninja invokes MX251.

Only creates a new evidence directory. Never downloads, installs, or deletes.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import time


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("profiles", "toolchain", "cmake", "ninja", "pwsh", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--leaf-call-feedback", action="store_true",
                        help="Use the separately pinned experimental leaf-feedback matrix")
    parser.add_argument("--cost-model-change", action="store_true",
                        help="Require permanent negative-case qualification before building the matrix")
    parser.add_argument("--cost-model-source", type=Path)
    parser.add_argument("--cost-model-adapter", type=Path)
    args = parser.parse_args()
    cost_model_change = args.cost_model_change or args.cost_model_source is not None or args.cost_model_adapter is not None
    if cost_model_change and (args.cost_model_source is None or args.cost_model_adapter is None):
        parser.error("Cost-model changes require both --cost-model-source and --cost-model-adapter")
    validation = Path(__file__).resolve().parent
    adapter = validation.parent / "build/qualified"
    matrix_path = validation / ("acceptance-leaf-feedback.json" if args.leaf_call_feedback else "acceptance.json")
    matrix = read_json(matrix_path)
    if bool(matrix.get("leafCallFeedback", False)) != args.leaf_call_feedback:
        raise ValueError("Qualification matrix/leaf-feedback mode mismatch")
    toolchain = args.toolchain.resolve()
    executables = {name: getattr(args, name).resolve() for name in ("cmake", "ninja", "pwsh")}
    for path in [*executables.values(), args.profiles, adapter / "Build.ps1"]:
        if not path.is_file():
            raise FileNotFoundError(path)
    if matrix["schema"] != 2 or digest(adapter / "TargetContracts.json") != matrix["targetContractsSha256"]:
        raise ValueError("Qualification matrix/target contract identity mismatch")
    # 路径映射显式由调用者给出；禁止重名覆盖、缺项或自动猜测器件。
    def unique_pairs(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise ValueError("Duplicate profile mapping: " + key)
            result[key] = value
        return result
    mapping = json.loads(args.profiles.read_text(encoding="utf-8-sig"), object_pairs_hook=unique_pairs)
    if not isinstance(mapping, dict) or set(mapping) != set(matrix["profiles"]):
        raise ValueError("Profile mappings must match all matrix targets exactly")
    profiles, identities = {}, {}
    for target, relative in mapping.items():
        if not isinstance(relative, str) or not relative:
            raise ValueError("Invalid profile path: " + target)
        profile = (args.profiles.resolve().parent / relative).resolve()
        expected = matrix["profiles"][target]
        lock_path = profile / "profile.lock.json"
        if digest(lock_path) != expected["profileLockSha256"]:
            raise ValueError("Qualification matrix/profile identity mismatch: " + target)
        lock = read_json(lock_path)
        compiler = (profile / lock["compilerOverlay"]["path"]).resolve()
        if (lock["target"] != target or not compiler.is_relative_to(profile) or
                digest(compiler) != expected["compilerSha256"] or digest(compiler) != matrix["compilerSha256"]):
            raise ValueError("Qualification compiler/target identity mismatch: " + target)
        profiles[target] = profile
        identities[target] = {"path": str(profile), **expected}
    contracts = read_json(adapter / "TargetContracts.json")["targets"]
    output = args.output.resolve()
    if any(output.is_relative_to(root) for root in [validation, toolchain, *profiles.values()]):
        raise ValueError("Output must be outside validation, profiles and toolchain inputs")
    output.mkdir(parents=True, exist_ok=False)
    environment = os.environ.copy()
    for key in ("CFLAGS", "CXXFLAGS", "LDFLAGS", "CPATH", "C_INCLUDE_PATH", "SDCC_HOME", "SDCC_INCLUDE", "SDCC_LIB"):
        environment.pop(key, None)
    environment.update(POWERSHELL_TELEMETRY_OPTOUT="1", POWERSHELL_UPDATECHECK="Off")
    commands, results = [], []
    summary = {"version": matrix["version"], "status": "running", "hardwareAccess": False,
               "costModelRegression": {"required": cost_model_change, "qualified": False,
                                       "status": "pending" if cost_model_change else "not-requested-not-model-qualification"},
               "matrixSha256": digest(matrix_path), "runnerSha256": digest(Path(__file__)),
               "leafCallFeedback": args.leaf_call_feedback,
               "compilerSha256": matrix["compilerSha256"], "profiles": identities,
               "targetContractsSha256": matrix["targetContractsSha256"],
               "tools": {k: {"path": str(p), "sha256": digest(p)} for k, p in executables.items()},
               "adapter": {p.name: digest(p) for p in adapter.iterdir() if p.is_file()}, "cases": results}

    def run(case_dir, label, command):
        command = list(map(str, command))
        start = time.monotonic()
        record = {"case": case_dir.name, "label": label, "command": command, "cwd": str(case_dir)}
        commands.append(record)
        try:
            with (case_dir / (label + ".stdout.log")).open("wb") as stdout, (case_dir / (label + ".stderr.log")).open("wb") as stderr:
                process = subprocess.run(command, cwd=case_dir, env=environment, stdout=stdout, stderr=stderr, timeout=900)
            record["exitCode"] = process.returncode
            return process.returncode
        finally:
            record["seconds"] = time.monotonic() - start
            write_json(output / "commands.json", commands)

    try:
        if cost_model_change:
            gate = output / "cost-model-regressions"
            code = run(output, "cost-model-gate", [sys.executable, "-B", validation / "CheckCostModelRegressions.py",
                       "--model-source", args.cost_model_source.resolve(), "--adapter", args.cost_model_adapter.resolve(),
                       "--output", gate])
            gate_result = read_json(gate / "result.json") if (gate / "result.json").is_file() else {}
            passed = code == 0 and gate_result.get("modelQualified") is True
            summary["costModelRegression"].update(qualified=passed, status=gate_result.get("status", "failed"))
            if not passed:
                raise RuntimeError("Cost model did not reject all permanent negative cases; matrix qualification stopped")
        for case in matrix["cases"]:
            profile = profiles[case["profile"]]
            profile_identity = matrix["profiles"][case["profile"]]
            source = validation / case["project"]
            case_dir = output / case["id"]
            case_dir.mkdir()
            project = case_dir / "project"
            # 复制整个固定工程，源码清单仍只由原工程 CMakeLists.txt 决定。
            shutil.copytree(source, project, ignore=shutil.ignore_patterns(".build", ".git", "__pycache__"))
            config = read_json(project / "mx251.json")
            config.update(case["overrides"])
            write_json(project / "mx251.json", config)
            record = {"id": case["id"], "status": "running", "configuration": config, "profile": case["profile"],
                      "inputs": {str(p.relative_to(project)): digest(p) for p in sorted(project.rglob("*")) if p.is_file()}}
            results.append(record)
            write_json(output / "results.json", summary)
            build = project / ".build/cmake"
            configure = [executables["cmake"], "-S", project, "-B", build, "-G", "Ninja",
                         "-DCMAKE_MAKE_PROGRAM=" + executables["ninja"].as_posix(),
                         "-DMX251_PROJECT_MODULE=" + (adapter / "Project.cmake").as_posix(),
                         "-DMX251_BUILD_SCRIPT=" + (adapter / "Build.ps1").as_posix(),
                         "-DMX251_PWSH=" + executables["pwsh"].as_posix(),
                         "-DMX251_LEAF_CALL_FEEDBACK=" + ("ON" if args.leaf_call_feedback else "OFF"),
                         "-DMX251_PROFILE=" + profile.as_posix(), "-DMX251_TOOLCHAIN=" + toolchain.as_posix()]
            if run(case_dir, "configure", configure):
                raise RuntimeError(case["id"] + ": CMake configuration failed; see original logs")
            code = run(case_dir, "build", [executables["cmake"], "--build", build, "--target", "firmware"])
            latest = project / ".build/mx251/latest"
            if "diagnostic" in case:
                logs = "\n".join(p.read_text(encoding="utf-8", errors="replace") for p in case_dir.glob("build.*.log"))
                if code == 0 or case["diagnostic"] not in logs:
                    raise RuntimeError(case["id"] + ": expected rejection was not observed")
                if any((latest / name).exists() for name in ("firmware.hex", "firmware.bin", "receipt.json")):
                    raise RuntimeError(case["id"] + ": rejected input published firmware or success receipt")
                record["diagnostic"] = case["diagnostic"]
            else:
                if code:
                    raise RuntimeError(case["id"] + ": build failed; see original logs")
                receipt = read_json(latest / "receipt.json")
                actual = digest(latest / "firmware.hex")
                expected = case["firmwareSha256"]
                contract = contracts[config["target"]]
                if (receipt["status"] != "complete" or receipt["schema"] != 4 or
                    actual != expected or receipt["firmwareSha256"] != actual or
                    receipt["codeBytes"] != case["codeBytes"] or
                    receipt["compilerSha256"] != matrix["compilerSha256"] or
                    receipt["profileLockSha256"] != profile_identity["profileLockSha256"] or
                    receipt["target"] != config["target"] or receipt["targetContract"]["id"] != config["target"] or
                    receipt["targetContract"]["registrySha256"] != matrix["targetContractsSha256"] or
                    receipt["targetContract"]["startupSha256"] != contract["startup"]["sha256"] or
                    receipt["targetContract"]["qualifiedMemory"] != contract["qualifiedMemory"] or
                    receipt["targetContract"]["interruptVectors"] != contract["interruptVectors"] or
                    receipt["interruptVectorCount"] != contract["interruptVectors"]["count"]):
                    raise RuntimeError(case["id"] + ": firmware, receipt, or tool identity differs from qualified baseline")
                record.update(firmwareSha256=actual, codeBytes=receipt["codeBytes"])
                feedback = receipt["leafCallFeedback"]
                if (feedback["enabled"] is not args.leaf_call_feedback or
                    feedback["finalValidated"] is not args.leaf_call_feedback or
                    feedback["scriptSha256"] != digest(adapter / "LeafCallFeedback.ps1")):
                    raise RuntimeError(case["id"] + ": leaf-feedback receipt mode or script identity mismatch")
                if args.leaf_call_feedback:
                    leaf = read_json(latest / "leaf-feedback.json")
                    if (feedback["recordSha256"] != digest(latest / "leaf-feedback.json") or
                        not leaf["finalValidated"] or leaf["compilerSha256"] != matrix["compilerSha256"] or
                        leaf["profileLockSha256"] != profile_identity["profileLockSha256"] or
                        leaf["feedbackSha256"] != digest(latest / "leaf-effects.txt")):
                        raise RuntimeError(case["id"] + ": final leaf-feedback binding mismatch")
                record["leafCallFeedback"] = feedback
            record["status"] = "passed"
            write_json(output / "results.json", summary)
            print("PASS " + case["id"], flush=True)
        summary["status"] = "passed"
    except Exception as error:
        summary.update(status="failed", error=str(error))
        if results and results[-1]["status"] == "running":
            results[-1]["status"] = "failed"
        raise
    finally:
        write_json(output / "results.json", summary)


if __name__ == "__main__":
    main()
