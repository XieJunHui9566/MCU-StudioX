"""在已核验的独立 C SDK 包中增加 MicroPython 模板，不下载或执行板上固件。"""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import zipfile


def digest(data):
    return hashlib.sha256(data).hexdigest()


def create(archive, chip, device_id, board, output, cli):
    root = output / (chip + "-0.2.0")
    source = root / "source"
    source.mkdir(parents=True)
    with zipfile.ZipFile(archive) as package:
        index = json.loads(package.read("files.sha256.json"))
        for relative, expected in index.items():
            path = PurePosixPath(relative)
            if path.is_absolute() or ".." in path.parts or "\\" in relative or ":" in relative:
                raise ValueError("Invalid archive path: " + relative)
            content = package.read(relative)
            if digest(content) != expected:
                raise ValueError("C SDK source hash mismatch: " + relative)
            target = source / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
    manifest_path = source / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
    expected_id = "raspberrypi." + chip.lower()
    if manifest["id"] != expected_id or manifest["version"] != "0.1.0":
        raise ValueError("Expected original C SDK pack 0.1.0: " + expected_id)
    device = next(item for item in manifest["devices"] if item["id"] == device_id)
    if any(item.get("microPython") for item in device["templates"]):
        raise ValueError("Input already contains MicroPython templates")
    recipe = Path(__file__).resolve().parent.parent / "examples/packs/raspberrypi.micropython"
    template_dir = source / "micropython"
    template_dir.mkdir()
    for name in ("minimal.py", "blink.py", "boot.py", "README.md"):
        shutil.copyfile(recipe / name, template_dir / name)
    for name, title, description in (
        ("minimal", "MicroPython · 最小脚本", "输出解释器信息和五次计数；不配置外部 GPIO。"),
        ("blink", "MicroPython · LED 闪灯", "使用官方 Pico / Pico 2 LED 名称，每 500 ms 翻转；兼容板需核对接线。"),
    ):
        device["templates"].append({
            "id": "micropython-" + name,
            "displayName": title,
            "description": description,
            "entryFile": "micropython/" + name + ".py",
            "files": {"boot.py": "micropython/boot.py", "README.md": "micropython/README.md"},
            "microPython": {"board": board, "version": "1.29.0"},
        })
    manifest["version"] = "0.2.0"
    manifest["displayName"] = chip + " · Pico SDK C / MicroPython"
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    provenance = {
        "basePack": {"id": expected_id, "version": "0.1.0", "sha256": digest(archive.read_bytes())},
        "microPythonVersion": "1.29.0", "board": board,
        "firmwarePage": "https://micropython.org/download/" + board + "/",
        "firmwareUrl": "https://micropython.org/resources/firmware/" + board + "-20260824-v1.29.0.uf2",
        "boardSource": "https://github.com/micropython/micropython/blob/v1.29.0/ports/rp2/boards/" + board + "/mpconfigboard.h",
        "documentation": "https://docs.micropython.org/en/v1.29.0/rp2/quickref.html",
        "firmwareBundled": False,
        "validation": "Offline software validation only; original C SDK sources and licenses retained.",
    }
    (template_dir / "provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    packed = root / (expected_id + "-0.2.0.mcupack")
    subprocess.run(["dotnet", str(cli), "pack", str(source), str(packed)], check=True)
    result = {"file": packed.name, "id": expected_id, "version": "0.2.0", "sha256": digest(packed.read_bytes()), "devices": [device_id]}
    (root / "index.json").write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--rp2040", type=Path, required=True)
    parser.add_argument("--rp2350", type=Path, required=True)
    parser.add_argument("--cli", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Output directory must be new")
    args.output.mkdir(parents=True)
    create(args.rp2040, "RP2040", "RP2040-PICO", "RPI_PICO", args.output, args.cli)
    create(args.rp2350, "RP2350", "RP2350A-PICO2", "RPI_PICO2", args.output, args.cli)


if __name__ == "__main__":
    main()
