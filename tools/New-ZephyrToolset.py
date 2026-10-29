"""从已准备的固定官方工作区制作独立组件，不安装全局 Python 或修改系统 PATH。"""

import argparse
import hashlib
import json
import shutil
import subprocess
import tarfile
from pathlib import Path

REPOSITORIES = {
    "zephyr": "dccb09599635bdff17633fa7e9dab014b91dce90",
    "modules/hal/cmsis": "512cc7e895e8491696b61f7ba8066b4a182569b8",
    "modules/hal/cmsis_6": "30a859f44ef8ab4dc8f84b03ed586fd16ccf9d74",
    "modules/hal/stm32": "fc11896dd39cfca37bf9b4aeaaa2df8861b81875",
}


def copy_tree(source, target):
    shutil.copytree(source, target, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", ".git"))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--workspace", type=Path, required=True)
    parser.add_argument("--native-tools", type=Path, required=True)
    parser.add_argument("--python", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    out = args.output.resolve()
    if out.exists():
        raise RuntimeError("Output already exists; choose a new component directory.")
    out.mkdir(parents=True)
    for relative, commit in REPOSITORIES.items():
        repo = args.workspace / relative
        head = subprocess.check_output(
            ["git", "-C", str(repo), "rev-parse", "HEAD"], text=True
        ).strip()
        if head != commit:
            raise RuntimeError(f"Unexpected source commit: {relative} / {head}")
        # git archive 只复制已锁定的跟踪内容，避免复制本机缓存或工作区改动。
        archive = out / "source.tar"
        with archive.open("wb") as stream:
            subprocess.run(["git", "-C", str(repo), "archive", commit], stdout=stream, check=True)
        target = out / relative
        target.mkdir(parents=True)
        with tarfile.open(archive) as stream:
            stream.extractall(target, filter="data")
        archive.unlink()
        print("Prepared", relative, flush=True)
    copy_tree(args.workspace / "sdk", out / "sdk")
    for relative in ("cmake", "ninja"):
        copy_tree(args.native_tools / relative, out / relative)
    python_target = out / "python"
    python_target.mkdir()
    for name in (
        "python.exe",
        "python3.dll",
        "python312.dll",
        "vcruntime140.dll",
        "vcruntime140_1.dll",
        "LICENSE.txt",
    ):
        shutil.copy2(args.python / name, python_target / name)
    copy_tree(args.python / "DLLs", python_target / "DLLs")
    shutil.copytree(
        args.python / "Lib",
        python_target / "Lib",
        ignore=shutil.ignore_patterns("site-packages", "__pycache__", "*.pyc"),
    )
    copy_tree(args.workspace / "venv/Lib/site-packages", python_target / "Lib/site-packages")
    for name in ("python-packages.freeze.txt", "sdk-sha256.sum"):
        shutil.copy2(args.workspace / name, out / name)
    (out / "PROVENANCE.md").write_text(
        "# Zephyr STM32 development component\n\n"
        "Zephyr v4.4.2; Zephyr SDK 1.0.1 / Arm GCC 14.3.0; Python 3.12.7.\n\n"
        "Source commits are recorded in provenance.json; SDK archive digests in sdk-sha256.sum.\n"
        "Python dependency versions are recorded in python-packages.freeze.txt.\n"
        "All upstream source and license files are retained. No system runtime installation is required.\n",
        encoding="utf-8",
    )
    manifest = dict(
        formatVersion=1,
        id="zephyr.stm32",
        version="4.4.2",
        host="win-x64",
        compilerId="zephyr-sdk-1.0.1-arm-14.3.0",
        displayName="Zephyr STM32 framework",
        purpose="zephyr",
        componentVersions=dict(zephyr="4.4.2", sdk="1.0.1", gcc="14.3.0", python="3.12.7"),
        resourceDirectories=dict(
            zephyr="zephyr",
            sdk="sdk",
            python="python",
            cmsis="modules/hal/cmsis",
            cmsis6="modules/hal/cmsis_6",
            halStm32="modules/hal/stm32",
        ),
        executables=dict(
            cmake="cmake/bin/cmake.exe", ninja="ninja/ninja.exe", python="python/python.exe"
        ),
    )
    for role, name in dict(
        gcc="gcc",
        gxx="g++",
        objcopy="objcopy",
        objdump="objdump",
        size="size",
        readelf="readelf",
        ar="ar",
        ranlib="ranlib",
    ).items():
        manifest["executables"][role] = f"sdk/gnu/arm-zephyr-eabi/bin/arm-zephyr-eabi-{name}.exe"
    # 来源和内容摘要随离线组件保存；工具路径均相对组件根目录。
    (out / "provenance.json").write_text(
        json.dumps(
            dict(
                repositories=REPOSITORIES,
                upstream="https://github.com/zephyrproject-rtos/zephyr/tree/v4.4.2",
                sdk="https://github.com/zephyrproject-rtos/sdk-ng/releases/tag/v1.0.1",
                note="Prepared from existing verified workspace; licenses retained in source trees and SDK.",
            ),
            indent=2,
        )
        + "\n",
        encoding="utf-8",
    )
    hashes = {}
    for file in sorted(out.rglob("*")):
        if file.is_file():
            with file.open("rb") as stream:
                hashes[file.relative_to(out).as_posix()] = hashlib.file_digest(
                    stream, "sha256"
                ).hexdigest()
    manifest["sha256"] = hashes
    (out / "toolset.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Prepared {len(manifest['sha256'])} indexed files", flush=True)


if __name__ == "__main__":
    main()
