"""从明确的本机资源生成 Linux 编译预览组件；不安装系统工具、不迁移 Windows 工程。"""

import argparse
import hashlib
import json
import pathlib
import shutil
import subprocess
import tarfile
import tempfile
import zipfile


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def version(executable, expected):
    actual = subprocess.check_output(
        [str(executable), "--version"], text=True
    ).splitlines()[0]
    if expected not in actual:
        raise ValueError(f"工具版本不匹配：{actual}；要求 {expected}")
    return actual


def prepare(args):
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    tools = output / "runtime/toolsets/arm.gnu/1.0.0"
    tools.mkdir(parents=True)
    expected_arm_hash = (
        "597893282ac8c6ab1a4073977f2362990184599643b4c5ee34870a8215783a16"
    )
    if sha256(args.arm_archive) != expected_arm_hash:
        raise ValueError("Arm 官方工具链归档哈希不匹配")
    with tempfile.TemporaryDirectory(prefix=".arm-extract-", dir=output) as temporary:
        with tarfile.open(args.arm_archive) as archive:
            archive.extractall(temporary, filter="data")
        arm_root = (
            pathlib.Path(temporary) / "arm-gnu-toolchain-15.2.rel1-x86_64-arm-none-eabi"
        )
        shutil.copytree(arm_root, tools / "gcc", symlinks=False)
    versions = {
        "gcc": version(tools / "gcc/bin/arm-none-eabi-gcc", "15.2.1"),
        "cmake": version(args.cmake, args.cmake_version),
        "ninja": version(args.ninja, args.ninja_version),
    }
    # 组件边界拒绝链接。复制时解析厂商内部别名，保留执行位、库、头文件和原始许可证。
    (tools / "cmake/bin").mkdir(parents=True)
    shutil.copy2(args.cmake, tools / "cmake/bin/cmake")
    shutil.copytree(
        args.cmake_share, tools / "cmake/share" / args.cmake_share.name, symlinks=False
    )
    (tools / "ninja").mkdir()
    shutil.copy2(args.ninja, tools / "ninja/ninja")
    (tools / "licenses").mkdir()
    shutil.copy2(args.cmake_license, tools / "licenses/cmake-copyright.txt")
    shutil.copy2(args.ninja_license, tools / "licenses/ninja-copyright.txt")
    provenance = {
        "purpose": "Ubuntu x64 Linux 编译预览",
        "armOrigin": "https://developer.arm.com/-/media/Files/downloads/gnu/15.2.rel1/binrel/arm-gnu-toolchain-15.2.rel1-x86_64-arm-none-eabi.tar.xz",
        "armArchiveSha256": expected_arm_hash,
        "cmakeOrigin": "Ubuntu 已安装 cmake 软件包；依赖本机系统动态库，尚非通用 Linux 发行组件",
        "cmakeSourceSha256": sha256(args.cmake),
        "ninjaOrigin": "Ubuntu 已安装 ninja-build 软件包",
        "ninjaSourceSha256": sha256(args.ninja),
        "versions": versions,
    }
    (tools / "provenance.json").write_text(
        json.dumps(provenance, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    executables = {
        role: "gcc/bin/arm-none-eabi-" + name
        for role, name in {
            "gcc": "gcc",
            "gxx": "g++",
            "ar": "ar",
            "ranlib": "ranlib",
            "as": "as",
            "ld": "ld",
            "objcopy": "objcopy",
            "objdump": "objdump",
            "size": "size",
            "readelf": "readelf",
        }.items()
    }
    executables.update(cmake="cmake/bin/cmake", ninja="ninja/ninja")
    manifest = {
        "formatVersion": 1,
        "id": "arm.gnu",
        "version": "1.0.0",
        "host": "linux-x64",
        "compilerId": "arm-gnu-15.2.rel1",
        "displayName": "Arm GNU Linux 编译预览",
        "executables": executables,
        "componentVersions": versions,
        "sha256": {
            str(path.relative_to(tools)): sha256(path)
            for path in sorted(tools.rglob("*"))
            if path.is_file()
        },
    }
    (tools / "toolset.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    tool_file_count = len(manifest["sha256"])
    pack_path = output / "studiox.stm32f407.linux-preview-0.1.3.mcupack"
    with zipfile.ZipFile(args.pack) as archive:
        files = {
            name: archive.read(name)
            for name in archive.namelist()
            if name != "files.sha256.json" and not name.endswith("/")
        }
    manifest = json.loads(files["manifest.json"])
    if (manifest["id"], manifest["version"]) != ("studiox.stm32f407", "0.1.3"):
        raise ValueError("预览只使用明确的 STM32F407 0.1.3 输入包")
    source_hash = sha256(args.pack)
    if (
        source_hash
        != "df30af07db39bb161a5aac7ae1aaf3cceb4769afd043741b6ecc797cfee2ae86"
    ):
        raise ValueError("STM32F407 输入包哈希不匹配")
    manifest.update(
        id="studiox.stm32f407.linux-preview",
        displayName=manifest["displayName"] + " · Linux 编译预览",
    )
    for device in manifest["devices"]:
        needs = device.get("developmentComponents") or [
            {
                "id": device["toolsetId"],
                "version": device["toolsetVersion"],
                "compilerId": device["compilerId"],
                "purpose": "工程编译",
            }
        ]
        for need in needs:
            need["host"] = "linux-x64"
        device["developmentComponents"] = needs
        for template in device["templates"]:
            for need in template.get("developmentComponents") or []:
                need["host"] = "linux-x64"
    files["manifest.json"] = json.dumps(manifest, ensure_ascii=False, indent=2).encode(
        "utf-8"
    )
    files["linux-preview-provenance.json"] = json.dumps(
        {
            "sourcePack": "studiox.stm32f407/0.1.3",
            "sourceSha256": source_hash,
            "change": "独立包身份与显式 linux-x64 组件声明；SDK、模板、启动与内存数据保留",
            "hardwareVerified": False,
        },
        ensure_ascii=False,
        indent=2,
    ).encode("utf-8")
    index = {
        name: hashlib.sha256(data).hexdigest() for name, data in sorted(files.items())
    }
    files["files.sha256.json"] = json.dumps(index, indent=2).encode("utf-8")
    with zipfile.ZipFile(pack_path, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, data in sorted(files.items()):
            archive.writestr(name, data)
    print(
        json.dumps(
            {
                "runtime": str(output / "runtime"),
                "pack": str(pack_path),
                "packSha256": sha256(pack_path),
                "toolsetIndexedFiles": tool_file_count,
            },
            ensure_ascii=False,
        ),
        flush=True,
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    for name in [
        "arm-archive",
        "cmake",
        "cmake-share",
        "cmake-license",
        "ninja",
        "ninja-license",
        "pack",
        "output",
    ]:
        parser.add_argument("--" + name, type=pathlib.Path, required=True)
    parser.add_argument("--cmake-version", required=True)
    parser.add_argument("--ninja-version", required=True)
    prepare(parser.parse_args())
