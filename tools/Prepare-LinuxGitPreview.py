"""复制明确选择的原生 Git，版本、来源与许可证留在独立测试组件中。"""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument("--executable", required=True)
parser.add_argument("--core", required=True)
parser.add_argument("--share", required=True)
parser.add_argument("--license", required=True)
parser.add_argument("--version", required=True)
parser.add_argument("--runtime", required=True)
args = parser.parse_args()
target = Path(args.runtime).resolve() / "git"
if target.exists():
    raise FileExistsError("Private Git destination already exists: " + str(target))
version = subprocess.check_output([args.executable, "--version"], text=True).strip()
if version != "git version " + args.version:
    raise ValueError("Unexpected source Git version: " + version)
target.mkdir(parents=True)
(target / "bin").mkdir()
shutil.copy2(args.executable, target / "bin/git")
shutil.copytree(args.core, target / "libexec/git-core", symlinks=False)
shutil.copytree(args.share, target / "share/git-core", symlinks=False)
shutil.copy2(args.license, target / "COPYRIGHT")
environment = os.environ.copy()
environment["GIT_EXEC_PATH"] = str(target / "libexec/git-core")
actual = subprocess.check_output(
    [str(target / "bin/git"), "--version"], env=environment, text=True
).strip()
if actual != version:
    raise ValueError("Copied private Git has changed identity")
hashes = {
    path.relative_to(target).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
    for path in sorted(target.rglob("*"))
    if path.is_file()
}
receipt = {
    "formatVersion": 1,
    "host": "linux-x64",
    "version": args.version,
    "origin": "Explicitly selected Ubuntu package files; copyright retained",
    "limitation": "Depends on the current Ubuntu system libraries; not a generic Linux distribution bundle",
    "sourceExecutable": args.executable,
    "sourceCore": args.core,
    "files": hashes,
}
(target / "git-provenance.json").write_text(json.dumps(receipt, indent=2), encoding="utf-8")
print(json.dumps({"privateGit": str(target), "version": actual, "files": len(hashes)}))
