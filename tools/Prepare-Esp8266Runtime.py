"""准备独立 ESP8266 RTOS SDK 3.4 组件；与 IDF 共享只读的主机工具文件。"""

import argparse
import hashlib
import json
import os
import runpy
import shutil
import urllib.request
import zipfile
from pathlib import Path

shared = runpy.run_path(str(Path(__file__).with_name("Prepare-EspressifRuntime.py")))
require_contained = shared["require_contained"]
copy_sdk = shared["copy_sdk"]
hash_and_deduplicate = shared["hash_and_deduplicate"]
run_python = shared["run_python"]

ARCHIVES = {
    "get-pip-3.8.py": "6ed6e98282a504ee0a6632856e16c39f222d313fc38be33de216d4afb6ac12f7",
    "ESP8266_RTOS_SDK-v3.4.zip": "1e6c0da481f844de8eeb85dfb2db9d7c130cffc2047549eb34b3deaf9bd515c1",
    "xtensa-lx106-elf-gcc8_4_0-esp-2020r3-win32.zip": "733b4da8723471b430f8692b943a7917ad5920b98e7bc6bdf9fb7617182c2b33",
    "python-3.8.10-embed-amd64.zip": "abbe314e9b41603dde0a823b76f5bbbe17b3de3e5ac4ef06b759da5466711271",
    "mconf-v4.6.0.0-idf-20190628-win32.zip": "1b8f17f48740ab669c13bd89136e8cc92efe0cd29872f0d6c44148902a2dc40c",
}
MCONF_URL = "https://dl.espressif.com/github_assets/espressif/kconfig-frontends/releases/download/v4.6.0.0-idf-20190628/mconf-v4.6.0.0-idf-20190628-win32.zip"
PYTHON_PACKAGES = [
    "setuptools==68.2.2",
    "wheel==0.42.0",
    "click==7.1.2",
    "pyserial==3.5",
    "future==0.18.3",
    "cryptography==3.4.8",
    "pyparsing==2.3.1",
    "pyelftools==0.26",
    "esptool==2.8",
    "cffi==1.17.1",
    "pyaes==1.6.1",
    "ecdsa==0.19.2",
    "pycparser==2.23",
    "six==1.17.0",
]
TOOLS_STATE_NOTICE = (
    "ESP8266 RTOS SDK 3.4 needs no immutable tools-state configuration.\n"
    "StudioX creates writable IDF_TOOLS_PATH in the project's .build/idf-tools-state.\n"
)


def checked_archive(directory, name):
    path = directory / name
    with path.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != ARCHIVES[name]:
        raise ValueError("发行归档 SHA-256 不匹配：" + name)
    return path


def extract_archive(archive, destination, prefix=""):
    destination.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as source:
        for item in source.infolist():
            name = item.filename.replace("\\", "/")
            if prefix:
                if not name.startswith(prefix):
                    continue
                name = name[len(prefix) :]
            if not name or item.is_dir():
                continue
            if name.startswith("/") or ".." in Path(name).parts or ":" in name:
                raise ValueError("发行归档包含越界路径：" + name)
            if item.external_attr >> 16 & 0o170000 == 0o120000:
                raise ValueError("发行归档包含符号链接：" + name)
            target = require_contained(destination, destination / name)
            target.parent.mkdir(parents=True, exist_ok=True)
            with source.open(item) as incoming, target.open("wb") as outgoing:
                shutil.copyfileobj(incoming, outgoing)


def link_shared_tree(source, destination):
    # 主机 CMake/Ninja/Git 不随芯片 ABI 改变；两套 SDK 仍各自锁定组件版本与哈希。
    for path in sorted(source.rglob("*")):
        if not path.is_file():
            continue
        target = require_contained(destination, destination / path.relative_to(source))
        target.parent.mkdir(parents=True, exist_ok=True)
        if target.exists():
            if os.path.samefile(path, target):
                continue
            target.unlink()
        try:
            os.link(path, target)
        except OSError:
            shutil.copy2(path, target)


def prepare_python(downloads, output):
    output.mkdir(parents=True, exist_ok=True)
    if not (output / "python.exe").is_file():
        extract_archive(
            checked_archive(downloads, "python-3.8.10-embed-amd64.zip"), output
        )
    for path in output.glob("*._pth"):
        require_contained(output, path).unlink()
    python = output / "python.exe"
    # Python 3.8 是该旧 SDK 的独立兼容环境，不污染 IDF 5.5 的 Python 依赖。
    try:
        run_python(python, ["-s", "-B", "-m", "pip", "--version"], output)
    except RuntimeError:
        run_python(
            python,
            [
                "-s",
                "-B",
                str((downloads / "get-pip-3.8.py").resolve()),
                "--no-cache-dir",
                "--target",
                str(output / "Lib" / "site-packages"),
                "pip==24.3.1",
            ],
            output,
        )
    run_python(
        python,
        [
            "-s",
            "-B",
            "-m",
            "pip",
            "install",
            "--no-cache-dir",
            "--target",
            str(output / "Lib" / "site-packages"),
            "--upgrade",
            *PYTHON_PACKAGES,
        ],
        output,
    )
    scripts = output / "Scripts"
    scripts.mkdir(exist_ok=True)
    # pip 的 Windows 包装器嵌入安装路径；产品只用解释器模块入口，不分发这些绝对路径。
    package_scripts = output / "Lib" / "site-packages" / "bin"
    if package_scripts.exists():
        shutil.rmtree(require_contained(output, package_scripts))
    for path in scripts.iterdir():
        if path.is_file():
            require_contained(output, path).unlink()
    for path in output.iterdir():
        if path.is_file() and path.suffix in {".exe", ".dll", ".pyd"}:
            shutil.copy2(path, scripts / path.name)
    for path in output.rglob("__pycache__"):
        shutil.rmtree(require_contained(output, path))
    return run_python(
        python, ["-s", "-B", "-c", "import esptool; print(esptool.__version__)"], output
    )


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-root", type=Path, required=True)
    parser.add_argument("--downloads", type=Path, required=True)
    parser.add_argument("--idf-runtime", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    output = arguments.output.resolve()
    source = arguments.sdk_root.resolve()
    sources = [source, arguments.idf_runtime.resolve(), arguments.downloads.resolve()]
    if output == Path(output.anchor) or any(
        output.is_relative_to(root) or root.is_relative_to(output) for root in sources
    ):
        raise ValueError("组件输出必须与来源目录分开，且不能是盘符根目录。")
    output.mkdir(parents=True, exist_ok=True)
    downloads = arguments.downloads.resolve()
    mconf = downloads / "mconf-v4.6.0.0-idf-20190628-win32.zip"
    if not mconf.exists():
        print("Downloading official legacy Windows Kconfig frontend...", flush=True)
        urllib.request.urlretrieve(MCONF_URL, mconf)
    for name in ARCHIVES:
        checked_archive(downloads, name)
    print("Preparing SDK 3.4 without examples or Git history...", flush=True)
    copy_sdk(source, output / "sdk", "3.4.0")
    print("Preparing separate Xtensa LX106 ABI...", flush=True)
    extract_archive(
        checked_archive(downloads, "xtensa-lx106-elf-gcc8_4_0-esp-2020r3-win32.zip"),
        output / "gcc",
        "xtensa-lx106-elf/",
    )
    extract_archive(checked_archive(downloads, mconf.name), output / "mconf")
    for folder in ("cmake", "ninja", "git"):
        link_shared_tree(arguments.idf_runtime.resolve() / folder, output / folder)
    print("Preparing locked Python 3.8 compatibility environment...", flush=True)
    esptool = prepare_python(downloads, output / "python")
    (output / "idf-tools").mkdir(exist_ok=True)
    # 文件复制/安装不会保留空目录；只读说明确保资源目录在开发版与发行版都存在。
    (output / "idf-tools" / "README.txt").write_text(
        TOOLS_STATE_NOTICE, encoding="utf-8"
    )
    executables = {
        "python": "python/python.exe",
        "cmake": "cmake/bin/cmake.exe",
        "ninja": "ninja/ninja.exe",
        "git": "git/cmd/git.exe",
        "mconf": "mconf/mconf-v4.6.0.0-idf-20190628-win32/mconf-idf.exe",
    }
    for role, suffix in (
        ("gcc", "gcc"),
        ("gxx", "g++"),
        ("objcopy", "objcopy"),
        ("objdump", "objdump"),
        ("size", "size"),
        ("ar", "ar"),
        ("ranlib", "ranlib"),
    ):
        executables[role] = f"gcc/bin/xtensa-lx106-elf-{suffix}.exe"
        executables[role + "-esp8266"] = executables[role]
    for path in executables.values():
        if not (output / path).is_file():
            raise FileNotFoundError(path)
    provenance = {
        "framework": "ESP8266_RTOS_SDK",
        "version": "3.4.0",
        "source": "https://github.com/espressif/ESP8266_RTOS_SDK/releases/tag/v3.4",
        "toolManifest": "https://raw.githubusercontent.com/espressif/ESP8266_RTOS_SDK/v3.4/tools/tools.json",
        "archivesSha256": ARCHIVES,
        "pythonPackages": PYTHON_PACKAGES,
        "pythonSource": "https://www.python.org/ftp/python/3.8.10/python-3.8.10-embed-amd64.zip",
        "layout": "Legacy SDK/compiler/Python isolated; immutable host tool files shared by hard links",
    }
    (output / "SOURCE.json").write_text(
        json.dumps(provenance, indent=2) + "\n", encoding="utf-8"
    )
    print("Indexing immutable legacy runtime...", flush=True)
    hashes, size, saved = hash_and_deduplicate(output)
    manifest = {
        "formatVersion": 1,
        "id": "espressif.esp8266-rtos",
        "version": "3.4.0",
        "host": "win-x64",
        "compilerId": "esp8266-rtos",
        "executables": executables,
        "sha256": hashes,
        "displayName": "Espressif ESP8266 RTOS SDK 3.4",
        "componentVersions": {
            "esp8266-rtos-sdk": "3.4.0",
            "gcc": "8.4.0-esp-2020r3",
            "python": "3.8.10",
            "cmake": "3.30.2",
            "ninja": "1.12.1",
            "esptool": esptool,
        },
        "resourceDirectories": {
            "idf": "sdk",
            "tools": "idf-tools",
            "python-env": "python",
        },
        "purpose": "esp8266-rtos-sdk",
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
