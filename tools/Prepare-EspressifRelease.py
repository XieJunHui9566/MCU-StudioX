"""手动准备指定官方 IDF 发行的隔离组件；不安装系统 Python、不改 PATH、不发布。"""

import argparse
import concurrent.futures
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import posixpath
import re
import shutil
import subprocess
import tarfile
import urllib.request
import zipfile


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def write_json(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def request_json(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "MCUStudioX-release-preparation"}), timeout=60) as response:
        return json.load(response)


def download(record, cache):
    url = record["url"]
    if not url.startswith(("https://github.com/", "https://dl.espressif.com/")):
        raise ValueError("Unapproved tool origin: " + url)
    file = cache / (record["sha256"] + ".zip")
    if not file.exists() or digest(file) != record["sha256"]:
        partial = file.with_suffix(".partial")
        with file.with_suffix(".log").open("ab") as log:
            result = subprocess.run(["curl.exe", "--fail", "--location", "--retry", "3", "--continue-at", "-", "--output", str(partial), url], stdout=log, stderr=log)
        if result.returncode:
            raise RuntimeError("Tool download failed; raw log: " + str(file.with_suffix(".log")))
        if partial.stat().st_size != record["size"] or digest(partial) != record["sha256"]:
            raise ValueError("Official tool digest mismatch: " + url)
        partial.rename(file)
    if file.stat().st_size != record["size"]:
        raise ValueError("Official tool length mismatch")
    return file


def extract(package, destination, strip=0, selected=None, sdk_links=False):
    destination.mkdir(parents=True, exist_ok=False)
    count = 0
    if not zipfile.is_zipfile(package):
        with tarfile.open(package, "r:gz") as archive:
            for entry in archive:
                if entry.isdir():
                    continue
                if not entry.isfile() or "\\" in entry.name or entry.name.startswith("/"):
                    raise ValueError("Unsafe TAR entry: " + entry.name)
                parts = entry.name.split("/")[strip:]
                if not parts or any(part in ("", ".", "..") or ":" in part for part in parts):
                    raise ValueError("Unsafe TAR path: " + entry.name)
                relative = "/".join(parts)
                if selected and not selected(relative):
                    continue
                target = destination.joinpath(*parts)
                if not target.resolve().is_relative_to(destination.resolve()):
                    raise ValueError("TAR boundary")
                target.parent.mkdir(parents=True, exist_ok=True)
                with archive.extractfile(entry) as source, target.open("xb") as output:
                    shutil.copyfileobj(source, output, 131072)
                count += 1
        return count
    with zipfile.ZipFile(package) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            if "\\" in entry.filename or entry.filename.startswith("/"):
                raise ValueError("Unsafe archive path: " + entry.filename)
            parts = entry.filename.split("/")[strip:]
            if not parts or any(part in ("", ".", "..") or ":" in part for part in parts):
                raise ValueError("Unsafe archive entry: " + entry.filename)
            relative = "/".join(parts)
            if any(part in (".git", "__pycache__") for part in parts) or selected and not selected(relative):
                continue
            if entry.external_attr >> 16 & 0o170000 == 0o120000:
                # 官方 NimBLE 的 RIOT 适配头使用一个包内相对链接；明确核对目标后实体化，组件归档仍只含普通文件。
                expected = "components/bt/host/nimble/nimble/porting/npl/riot/include/npl_syscfg/npl_sycfg.h"
                if not sdk_links or relative != expected or archive.read(entry) != b"../syscfg/syscfg.h":
                    raise ValueError("Unreviewed archive link: " + entry.filename)
                source_name = posixpath.normpath(posixpath.join(posixpath.dirname(entry.filename), "../syscfg/syscfg.h"))
                source_entry = archive.getinfo(source_name)
                if not source_name.startswith(entry.filename.split("/")[0] + "/components/") or source_entry.external_attr >> 16 & 0o170000 == 0o120000:
                    raise ValueError("SDK link must resolve to a regular file inside the SDK")
            else:
                source_entry = entry
            target = destination.joinpath(*parts)
            if not target.resolve().is_relative_to(destination.resolve()):
                raise ValueError("Archive boundary")
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(source_entry) as source, target.open("xb") as output:
                shutil.copyfileobj(source, output, 131072)
            count += 1
    return count


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--tag", required=True)
    parser.add_argument("--release-json", type=Path, required=True)
    parser.add_argument("--sdk-archive", type=Path, required=True)
    parser.add_argument("--base-component", type=Path, required=True)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if not re.fullmatch(r"v[56]\.\d+(?:\.\d+)?", args.tag):
        raise ValueError("Specify a stable exact release tag")
    output = args.output.resolve()
    base = args.base_component.resolve()
    cache = args.cache.resolve()
    if output.exists() or output == Path(output.anchor) or output.is_relative_to(base) or base.is_relative_to(output):
        raise ValueError("Use a new isolated output directory outside the original component")
    output.mkdir(parents=True)
    cache.mkdir(parents=True, exist_ok=True)
    spec_loader = importlib.util.spec_from_file_location("studiox_runtime_recipe", Path(__file__).with_name("Prepare-EspressifRuntime.py"))
    recipe = importlib.util.module_from_spec(spec_loader)
    spec_loader.loader.exec_module(recipe)
    release = json.loads(args.release_json.read_text(encoding="utf-8-sig"))
    if release["tag_name"] != args.tag or release["prerelease"] or release["draft"]:
        raise ValueError("Only the selected stable official release is accepted")
    asset = next(item for item in release["assets"] if item["name"] == "esp-idf-" + args.tag + ".zip")
    sha = digest(args.sdk_archive)
    if args.sdk_archive.stat().st_size != asset["size"] or asset["digest"] != "sha256:" + sha:
        raise ValueError("SDK archive differs from the official Release digest")
    print("Extracting verified SDK " + args.tag, flush=True)
    sdk = output / "source/sdk"
    extract(args.sdk_archive, sdk, strip=1, sdk_links=True, selected=lambda path: not path.startswith(("docs/", "examples/"))
        or path.startswith(("examples/get-started/hello_world/", "examples/system/freertos/real_time_stats/")))
    version_cmake = (sdk / "tools/cmake/version.cmake").read_text(encoding="utf-8")
    version = ".".join(re.search(r"set\(IDF_VERSION_" + part + r"\s+(\d+)\)", version_cmake).group(1) for part in ("MAJOR", "MINOR", "PATCH"))
    if args.tag.removeprefix("v").split(".") != version.split(".")[:len(args.tag.removeprefix("v").split("."))]:
        raise ValueError("SDK source version does not match the official tag")
    tools_spec = json.loads((sdk / "tools/tools.json").read_text(encoding="utf-8"))
    needed = {"xtensa-esp-elf", "riscv32-esp-elf", "xtensa-esp-elf-gdb", "riscv32-esp-elf-gdb", "cmake", "ninja", "esp32ulp-elf", "openocd-esp32", "esp-rom-elfs"}
    records = []
    for tool in tools_spec["tools"]:
        if tool["name"] not in needed:
            continue
        release_tool = next(item for item in tool["versions"] if item["status"] == "recommended")
        archive = release_tool.get("win64", release_tool.get("any"))
        if not archive or not archive["url"].endswith((".zip", ".tar.gz")):
            raise ValueError("A different archive format needs an explicit extraction recipe: " + tool["name"])
        records.append(dict(archive, name=tool["name"], version=release_tool["name"]))
    if {item["name"] for item in records} != needed:
        raise ValueError("SDK does not declare all required tool recipes")
    downloads = cache / "downloads"
    downloads.mkdir(exist_ok=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as workers:
        files = list(workers.map(lambda record: download(record, downloads), records))
    tools = cache / "tools"
    for record, archive in zip(records, files):
        target = tools / record["name"] / record["version"]
        receipt = cache / "receipts" / (record["name"] + "-" + record["version"] + ".json")
        if receipt.exists():
            if json.loads(receipt.read_text())["sha256"] != record["sha256"] or not target.is_dir():
                raise ValueError("Tool extraction receipt conflicts with selected official release")
        else:
            print("Extracting " + record["name"] + " " + record["version"], flush=True)
            extract(archive, target, strip=1 if record["name"] == "cmake" else 0)
            write_json(receipt, record)
    constraints_name = "espidf.constraints.v" + ".".join(version.split(".")[:2]) + ".txt"
    constraints_url = "https://dl.espressif.com/dl/esp-idf/" + constraints_name
    constraints = cache / constraints_name
    if not constraints.exists():
        urllib.request.urlretrieve(constraints_url, constraints)
    python_base = tools / "idf-python/3.11.2"
    base_manifest = json.loads((base / "toolset.json").read_text(encoding="utf-8"))
    # 复用已安装组件的标准库和解释器前逐文件校验；不把原组件的旧 site-packages 带入新 SDK。
    if not python_base.exists():
        for file in (base / "python").rglob("*"):
            if not file.is_file():
                continue
            relative = file.relative_to(base / "python")
            if relative.parts[0] == "Scripts" or relative.parts[:2] == ("Lib", "site-packages") or "__pycache__" in relative.parts:
                continue
            if digest(file) != base_manifest["sha256"].get("python/" + relative.as_posix()):
                raise ValueError("Original Python resource changed: " + str(relative))
            target = python_base / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(file, target)
    packages = output / "source/python-env"
    packages.mkdir()
    # 使用单独准备的 pip，不更新原 IDE 组件；其 wheel 由官方 PyPI 元数据绑定 SHA-256。
    pip_receipt = cache / "pip-source.json"
    if pip_receipt.exists():
        pip_source = json.loads(pip_receipt.read_text(encoding="utf-8"))
        wheel = cache / pip_source["filename"]
        if digest(wheel) != pip_source["digests"]["sha256"]:
            raise ValueError("Isolated pip bootstrap wheel changed")
    else:
        pip_source = next(item for item in request_json("https://pypi.org/pypi/pip/json")["urls"] if item["filename"].endswith("py3-none-any.whl"))
        wheel = cache / pip_source["filename"]
        urllib.request.urlretrieve(pip_source["url"], wheel)
        if digest(wheel) != pip_source["digests"]["sha256"]:
            raise ValueError("Official pip bootstrap digest mismatch")
        write_json(pip_receipt, pip_source)
    bootstrap = python_base / "Lib/site-packages"
    if not (bootstrap / "pip").exists():
        extract(wheel, bootstrap)
    backend_receipt = cache / "bootstrap-build-tools.json"
    if not backend_receipt.exists():
        recipe.run_python(python_base / "python.exe", ["-m", "pip", "--isolated", "install", "--disable-pip-version-check",
            "--only-binary=:all:", "--index-url", "https://pypi.org/simple/", "--target", str(bootstrap),
            "setuptools>=64", "wheel", "--report", str(backend_receipt)], output)
    print("Resolving release-specific Python dependencies " + version, flush=True)
    pip_log = recipe.run_python(python_base / "python.exe", ["-m", "pip", "--isolated", "install", "--disable-pip-version-check", "--only-binary=:all:",
        "--no-binary=esptool", "--no-build-isolation", "--cache-dir", str(cache / "pip-cache"),
        "--index-url", "https://pypi.org/simple/",
        "--target", str(packages / "Lib/site-packages"), "--constraint", str(constraints), "--requirement", str(sdk / "tools/requirements/requirements.core.txt"),
        "--report", str(packages / "pip-report.json")], output)
    (output / "python-dependencies.log").write_text(pip_log, encoding="utf-8")
    commit = request_json("https://api.github.com/repos/espressif/esp-idf/commits/" + args.tag)["sha"]
    source_receipt = {"sdkVersion": version, "upstreamTag": args.tag, "upstreamCommit": commit,
        "sdkArchive": {"url": asset["browser_download_url"], "sha256": sha, "bytes": asset["size"]}, "tools": records,
        "constraints": {"url": constraints_url, "sha256": digest(constraints)}, "sdkLinkMaterialization": "NimBLE RIOT npl_syscfg/npl_sycfg.h -> ../syscfg/syscfg.h, verified within SDK",
        "pipBootstrap": {"url": pip_source["url"], "sha256": pip_source["digests"]["sha256"], "version": pip_source["filename"]},
        "pythonBuildBackend": json.loads(backend_receipt.read_text(encoding="utf-8")),
        "pythonBase": {"id": base_manifest["id"], "version": base_manifest["version"],
            "manifestSha256": digest(base / "toolset.json")}, "publication": "local candidate; redistribution review is separate"}
    write_json(sdk / "release-source.json", source_receipt)
    templates = json.loads((Path(__file__).resolve().parents[1] / "examples/packs/espressif/idf-template-sources.json").read_text(encoding="utf-8"))
    templates.update(sdkVersion=version, upstreamTag=args.tag, upstreamCommit=commit)
    templates["filesSha256"] = {path: hashlib.sha256((sdk / path).read_bytes().replace(b"\r\n", b"\n")).hexdigest() for path in templates["filesSha256"]}
    for example in templates["examples"]:
        example["description"] = example["description"].replace("5.5.4", version)
    write_json(output / "template-sources.json", templates)
    runtime = output / "runtime/toolsets/espressif.idf" / version
    print("Assembling immutable component " + version, flush=True)
    environment = os.environ.copy()
    environment.update(PYTHONDONTWRITEBYTECODE="1", PYTHONNOUSERSITE="1")
    subprocess.run([str(base / "python/python.exe"), "-B", str(Path(__file__).with_name("Prepare-EspressifRuntime.py")), "--idf-root", str(sdk),
        "--tools-root", str(tools), "--python-env", str(packages), "--git-root", str(base / "git"), "--output", str(runtime),
        "--sdk-version", version, "--component-version", version, "--upstream-tag", args.tag], check=True, env=environment)
    write_json(output / "preparation-result.json", {"success": True, "hardware": False, "sdkVersion": version, "componentVersion": version, "upstreamTag": args.tag, "source": source_receipt})


if __name__ == "__main__":
    main()
