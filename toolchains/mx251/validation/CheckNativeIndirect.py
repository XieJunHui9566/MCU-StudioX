"""Offline native-call boundary checks; no device access or environment install."""
from pathlib import Path
import argparse
import hashlib
import json
import os
import subprocess

CASES = {
    "ordinary-caller": ("u32 add(u32 a,u32 b) ABI{return a+b;} typedef u32(*Fn)(u32,u32) ABI;Fn volatile fp=add;u32 run(u32 a,u32 b) __reentrant{return fp(a,b)+a;}", None),
    "native-caller": ("u32 add(u32 a,u32 b) ABI{return a+b;} typedef u32(*Fn)(u32,u32) ABI;Fn volatile fp=add;u32 run(u32 a,u32 b) ABI{return fp(a,b)+a;}", None),
    "stack-target": ("typedef u32(*Fn)(u32,u32) ABI;u32 run(Fn fp,u32 a,u32 b) __reentrant{return fp(a,b)+a;}", None),
    "pointer-return": ("typedef u32 __xdata *(*Fn)(u32 __xdata*,u32 __xdata*) ABI;extern Fn fp;u32 __xdata *run(u32 __xdata *a,u32 __xdata *b) __reentrant{return fp(a,b);}", None),
    "abi3-rejected": ("typedef u32(*Fn)(u32,u32) __reentrant __sdcccall(3);extern Fn fp;u32 run(u32 a,u32 b){return fp(a,b);}", "error 365"),
    "varargs-rejected": ("u32 run(u32 a,...) ABI{return a;}", "error 365"),
    "pointer-argument-rejected": ("typedef u32(*Fn)(u32,u32) ABI;u32 run(Fn fp,u32 a) ABI{return fp(a,a);}", "error 365"),
    "pointer-type-mismatch": ("u32 add(u32,u32) ABI;typedef u32(*Fn)(u32,u32) __reentrant;Fn fp=add;", "error 78"),
    "declaration-mismatch": ("u32 add(u32,u32) ABI;u32 add(u32 a,u32 b) __reentrant{return a+b;}", "error 98"),
    "no-prototype": ("typedef u32(*Fn)() ABI;extern Fn fp;u32 run(u32 a,u32 b){return fp(a,b);}", "error 101"),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("compiler", "toolchain", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    args = parser.parse_args()
    compiler, tools, output = args.compiler.resolve(), args.toolchain.resolve(), args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    environment = os.environ.copy()
    environment["SDCC_HOME"] = str(tools)
    environment["PATH"] = str(tools / "bin") + os.pathsep + environment["PATH"]
    rows = []
    for model in ("small", "large"):
        for name, (body, diagnostic) in CASES.items():
            directory = output / (model + "-" + name)
            directory.mkdir()
            (directory / "probe.c").write_text("typedef unsigned long u32;\n#define ABI __reentrant __sdcccall(4)\n" + body + "\n", encoding="utf-8")
            command = [str(compiler), "-mmcs251", "--model-" + model, "--stack-auto", "--mcs251-native-abi", "--mcs251-wide-ordinary", "-c", "probe.c"]
            result = subprocess.run(command, cwd=directory, env=environment, capture_output=True, timeout=30)
            (directory / "stdout.log").write_bytes(result.stdout)
            (directory / "stderr.log").write_bytes(result.stderr)
            log = result.stderr.decode(errors="replace")
            # 不支持的接口必须明确拒绝，不能留下对象或把后端崩溃误判成有效诊断。
            passed = (result.returncode == 0 and (directory / "probe.rel").exists()) if diagnostic is None else (
                result.returncode == 1 and diagnostic in log and not (directory / "probe.rel").exists())
            passed = passed and not any(word in log for word in ("Assertion", "Internal Error", "SIGSEGV"))
            rows.append(dict(case=name, model=model, passed=passed, exitCode=result.returncode, expectedDiagnostic=diagnostic, command=command))
            summary = dict(compilerSha256=hashlib.sha256(compiler.read_bytes()).hexdigest(), cases=rows)
            (output / "results.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
            if not passed:
                raise RuntimeError(directory.name + ": see original compiler logs")
    print("PASS", len(rows), "native indirect call boundaries")


if __name__ == "__main__":
    main()
