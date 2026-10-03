"""从已核实的 SDK 和工具发行目录准备只读内置组件，不复制工程或 Git 历史。"""

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import urllib.request
from pathlib import Path


IGNORED_DIRECTORIES = {".git", "__pycache__", ".pytest_cache", ".mypy_cache"}


def require_contained(root, path):
    root = root.resolve()
    path = path.resolve()
    if not path.is_relative_to(root):
        raise ValueError(f"输出路径越界：{path}")
    return path


def copy_tree(source, destination):
    if not source.is_dir():
        raise FileNotFoundError(source)
    for current, directories, files in os.walk(source):
        directories[:] = sorted(
            name for name in directories if name not in IGNORED_DIRECTORIES
        )
        relative = Path(current).relative_to(source)
        target = destination / relative
        target.mkdir(parents=True, exist_ok=True)
        for name in sorted(files):
            path = Path(current) / name
            # Espressif 的嵌入式 Python 标准库包含只有 .pyc 的发行文件；它们必须纳入只读索引。
            if path.suffix == ".pyo" or name in {".git", "direct_url.json"}:
                continue
            if path.is_symlink():
                raise ValueError(f"发行目录包含未经处理的链接：{path}")
            output = require_contained(destination, target / name)
            if output.exists() and output.stat().st_size == path.stat().st_size:
                if output.stat().st_mtime_ns == path.stat().st_mtime_ns:
                    continue
            shutil.copy2(path, output)


def copy_sdk(source, output, version):
    for name in ("components", "tools"):
        copy_tree(source / name, output / name)
    for path in sorted(source.iterdir()):
        if path.is_file() and (
            path.name
            in {
                "CMakeLists.txt",
                "Kconfig",
                "LICENSE",
                "README.md",
                "README_CN.md",
                "requirements.txt",
                "sdkconfig.rename",
            }
            or path.name.startswith("sdkconfig.rename.")
        ):
            shutil.copy2(path, output / path.name)
    # 厂商 CMake 原生支持版本文件；无需把可变的 Git 历史塞入每份发行组件。
    (output / "version.txt").write_text(
        "v" + version.rstrip(".0") if version == "3.4.0" else "v" + version,
        encoding="utf-8",
    )


def prepare_python(base, packages, output):
    copy_tree(base, output)
    copy_tree(packages, output / "Lib" / "site-packages")
    # _pth 会忽略禁写缓存和用户隔离环境变量；由宿主通过锁定的 PYTHONHOME 选择这个资源目录。
    for path in output.glob("*._pth"):
        require_contained(output, path).unlink()
    scripts = output / "Scripts"
    scripts.mkdir(exist_ok=True)
    for path in output.iterdir():
        if path.is_file() and path.suffix in {".exe", ".dll", ".pyd"}:
            target = scripts / path.name
            if not target.exists() or not os.path.samefile(path, target):
                shutil.copy2(path, target)
    for path in scripts.glob("*._pth"):
        require_contained(output, path).unlink()
    for path in output.rglob("__pycache__"):
        shutil.rmtree(require_contained(output, path))


def hash_and_deduplicate(output):
    hashes = {}
    shared = {}
    bytes_total = 0
    bytes_shared = 0
    for path in sorted(output.rglob("*")):
        if not path.is_file() or path.name == "toolset.json":
            continue
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        size = path.stat().st_size
        hashes[path.relative_to(output).as_posix()] = digest
        bytes_total += size
        # 同一只读组件内相同内容只占一份磁盘块；索引仍校验每个逻辑路径。
        key = (size, digest)
        if size >= 65536 and key in shared:
            temporary = require_contained(
                output, path.with_name(path.name + ".studiox-link")
            )
            try:
                os.link(shared[key], temporary)
                os.replace(temporary, path)
                bytes_shared += size
            except OSError:
                if temporary.exists():
                    temporary.unlink()
        else:
            shared[key] = path
    return hashes, bytes_total, bytes_shared


def run_python(python, arguments, output):
    environment = os.environ.copy()
    for name in ("PYTHONHOME", "PYTHONPATH", "PYTHONPYCACHEPREFIX"):
        environment.pop(name, None)
    # 打包脚本也不能继承用户的 pip.ini 或 PIP_TARGET，把依赖误装到另一套工程/系统 Python。
    for name in tuple(environment):
        if name.upper().startswith("PIP_"):
            environment.pop(name)
    environment["PIP_CONFIG_FILE"] = os.devnull
    environment["PYTHONDONTWRITEBYTECODE"] = "1"
    environment["PYTHONNOUSERSITE"] = "1"
    environment["PYTHONHOME"] = str(python.parent)
    if arguments[:2] == ["-m", "pip"]:
        # requests 不读取 Windows 代理注册项；仅给当前依赖准备进程传入系统已有代理，不改系统设置或输出其值。
        for scheme, proxy in urllib.request.getproxies().items():
            if scheme in {"http", "https"}:
                environment[scheme.upper() + "_PROXY"] = proxy
    completed = subprocess.run(
        [str(python), *arguments],
        cwd=output,
        env=environment,
        capture_output=True,
        text=True,
    )
    if completed.returncode:
        raise RuntimeError(completed.stdout + completed.stderr)
    return completed.stdout.strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--idf-root", type=Path, required=True)
    parser.add_argument("--tools-root", type=Path, required=True)
    parser.add_argument("--python-env", type=Path, required=True)
    parser.add_argument("--git-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--sdk-version", default="5.5.4")
    parser.add_argument("--component-version")
    parser.add_argument("--upstream-tag")
    parser.add_argument("--python-version", default="3.11.2")
    arguments = parser.parse_args()
    sdk_version = arguments.sdk_version
    component_version = arguments.component_version or sdk_version
    if not re.fullmatch(r"[56]\.\d+\.\d+", sdk_version) or not re.fullmatch(r"\d+\.\d+\.\d+", component_version):
        raise ValueError("Specify exact SDK and component release versions")
    version_cmake = (arguments.idf_root / "tools/cmake/version.cmake").read_text(encoding="utf-8")
    source_version = ".".join(re.search(r"set\(IDF_VERSION_" + part + r"\s+(\d+)\)", version_cmake).group(1)
        for part in ("MAJOR", "MINOR", "PATCH"))
    if source_version != sdk_version:
        raise ValueError("SDK source version differs from the requested release: " + source_version)
    spec = json.loads((arguments.idf_root / "tools/tools.json").read_text(encoding="utf-8"))
    def recommended(name):
        tool = next(tool for tool in spec["tools"] if tool["name"] == name)
        return next(version["name"] for version in tool["versions"] if version["status"] == "recommended")
    tool_version = recommended("xtensa-esp-elf")
    if recommended("riscv32-esp-elf") != tool_version:
        raise ValueError("Different compiler revisions need an explicit layout recipe")
    gdb_version = recommended("xtensa-esp-elf-gdb")
    if recommended("riscv32-esp-elf-gdb") != gdb_version:
        raise ValueError("Different GDB revisions need an explicit layout recipe")
    cmake_version = recommended("cmake")
    ninja_version = recommended("ninja")
    upstream_tag = arguments.upstream_tag or "v" + sdk_version
    output = arguments.output.resolve()
    sources = [
        arguments.idf_root,
        arguments.tools_root,
        arguments.python_env,
        arguments.git_root,
    ]
    if output == Path(output.anchor) or any(
        output.is_relative_to(source.resolve())
        or source.resolve().is_relative_to(output)
        for source in sources
    ):
        raise ValueError("组件输出必须在来源目录外，且不能是盘符根目录。")
    if (output / "toolset.json").exists():
        raise ValueError("An indexed component is immutable; select a new output directory")
    output.mkdir(parents=True, exist_ok=True)
    print(f"Preparing ESP-IDF {sdk_version} SDK (without history/examples)...", flush=True)
    copy_sdk(arguments.idf_root, output / "sdk", sdk_version)
    sources = {
        "xtensa": arguments.tools_root
        / "xtensa-esp-elf"
        / tool_version
        / "xtensa-esp-elf",
        "riscv": arguments.tools_root
        / "riscv32-esp-elf"
        / tool_version
        / "riscv32-esp-elf",
        "cmake": arguments.tools_root / "cmake" / cmake_version,
        "ninja": arguments.tools_root / "ninja" / ninja_version,
        "git": arguments.git_root,
    }
    for role, source in sources.items():
        print(f"Preparing shared {role}...", flush=True)
        copy_tree(source, output / role)
    # CoreDump 分析使用与 IDF tools.json 对应的官方 GDB，不能回退到系统工具。
    for folder, package in (("xtensa-gdb", "xtensa-esp-elf-gdb"), ("riscv-gdb", "riscv32-esp-elf-gdb")):
        copy_tree(arguments.tools_root / package / gdb_version / package, output / folder)
    print("Preparing relocatable Python...", flush=True)
    prepare_python(
        arguments.tools_root / "idf-python" / arguments.python_version,
        arguments.python_env / "Lib" / "site-packages",
        output / "python",
    )
    state = output / "idf-tools"
    state.mkdir(exist_ok=True)
    shutil.copy2(
        arguments.tools_root.parent / ("espidf.constraints.v" + ".".join(sdk_version.split(".")[:2]) + ".txt"),
        state / ("espidf.constraints.v" + ".".join(sdk_version.split(".")[:2]) + ".txt"),
    )
    esptool = run_python(
        output / "python" / "python.exe",
        ["-c", "import importlib.metadata as m; print(m.version('esptool'))"],
        output,
    )
    executables = {
        "python": "python/python.exe",
        "cmake": "cmake/bin/cmake.exe",
        "ninja": "ninja/ninja.exe",
        "git": "git/cmd/git.exe",
        "gdb-esp32": "xtensa-gdb/bin/xtensa-esp32-elf-gdb.exe",
        "gdb-esp32s3": "xtensa-gdb/bin/xtensa-esp32s3-elf-gdb.exe",
        "gdb-riscv": "riscv-gdb/bin/riscv32-esp-elf-gdb.exe",
    }
    for target, folder, prefix in (
        ("esp32", "xtensa", "xtensa-esp32-elf"),
        ("esp32s3", "xtensa", "xtensa-esp32s3-elf"),
        ("riscv", "riscv", "riscv32-esp-elf"),
    ):
        for role, suffix in (
            ("gcc", "gcc"),
            ("gxx", "g++"),
            ("objcopy", "objcopy"),
            ("objdump", "objdump"),
            ("size", "size"),
            ("ar", "ar"),
            ("ranlib", "ranlib"),
        ):
            executables[f"{role}-{target}"] = f"{folder}/bin/{prefix}-{suffix}.exe"
    for role in ("gcc", "gxx", "objcopy", "objdump", "size", "ar", "ranlib"):
        executables[role] = executables[f"{role}-esp32"]
    resources = {"idf": "sdk", "tools": "idf-tools", "python-env": "python"}
    extra_versions = {}
    for package, folder, role, executable in (
        ("esp32ulp-elf", "ulp", "ulp-as", "bin/esp32ulp-elf-as.exe"),
        ("openocd-esp32", "openocd", "openocd", "bin/openocd.exe"),
    ):
        revision = recommended(package)
        directory = arguments.tools_root / package / revision
        if directory.exists():
            entries = list(directory.iterdir())
            source = entries[0] if len(entries) == 1 and entries[0].is_dir() else directory
            copy_tree(source, output / folder)
            executables[role] = folder + "/" + executable
            extra_versions[package] = revision
    rom_revision = recommended("esp-rom-elfs")
    rom_source = arguments.tools_root / "esp-rom-elfs" / rom_revision
    if rom_source.exists():
        copy_tree(rom_source, output / "rom-elfs")
        resources["rom-elfs"] = "rom-elfs"
        extra_versions["esp-rom-elfs"] = rom_revision
    for relative in executables.values():
        if not (output / relative).is_file():
            raise FileNotFoundError(relative)
    provenance = {
        "framework": "ESP-IDF",
        "version": sdk_version,
        "sources": [
            f"https://github.com/espressif/esp-idf/releases/tag/{upstream_tag}",
            f"https://raw.githubusercontent.com/espressif/esp-idf/{upstream_tag}/tools/tools.json",
        ],
        "layout": "One SDK and two compiler trees shared by all six targets; no Git history or per-project SDK copies",
        "python": arguments.python_version,
        "esptool": esptool,
        "gcc": tool_version,
    }
    if (arguments.idf_root / "release-source.json").exists():
        provenance["releaseSource"] = json.loads((arguments.idf_root / "release-source.json").read_text(encoding="utf-8"))
    if (arguments.python_env / "pip-report.json").exists():
        shutil.copy2(arguments.python_env / "pip-report.json", output / "python-packages.json")
    (output / "SOURCE.json").write_text(
        json.dumps(provenance, indent=2) + "\n", encoding="utf-8"
    )
    print("Indexing and deduplicating immutable runtime files...", flush=True)
    hashes, size, saved = hash_and_deduplicate(output)
    manifest = {
        "formatVersion": 1,
        "id": "espressif.idf",
        "version": component_version,
        "host": "win-x64",
        "compilerId": "esp-idf",
        "executables": executables,
        "sha256": hashes,
        "displayName": f"Espressif ESP-IDF {sdk_version}",
        "componentVersions": {
            "esp-idf": sdk_version,
            "gcc": tool_version,
            "python": arguments.python_version,
            "cmake": cmake_version,
            "ninja": ninja_version,
            "esptool": esptool,
            "gdb": gdb_version,
            **extra_versions,
        },
        "resourceDirectories": resources,
        "purpose": "esp-idf",
    }
    (output / "toolset.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8"
    )
    print(
        json.dumps(
            {
                "files": len(hashes),
                "logicalGiB": round(size / 1024**3, 3),
                "deduplicatedGiB": round(saved / 1024**3, 3),
            }
        ),
        flush=True,
    )


if __name__ == "__main__":
    main()
