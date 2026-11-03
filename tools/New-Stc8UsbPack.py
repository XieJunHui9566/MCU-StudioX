"""Extend the reviewed STC pack with the exact STC8H8K64U memory profile."""

import argparse
import copy
import json
import subprocess
import sys
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-pack", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    recipe_path = (
        Path(__file__).resolve().parent.parent
        / "examples/packs/stc.stc8/stc8h8k64u.json"
    )
    recipe = json.loads(recipe_path.read_text(encoding="utf-8"))
    if (
        recipe["codeBytes"] + recipe["eepromBytes"] != recipe["physicalFlashBytes"]
        or recipe["ramBytes"] != recipe["xramBytes"] + 256
    ):
        raise ValueError("Inconsistent exact-device memory profile.")
    subprocess.run(
        [
            sys.executable,
            str(Path(__file__).with_name("New-StcStdintPack.py")),
            "--base-pack",
            str(args.base_pack),
            "--output",
            str(args.output),
        ],
        check=True,
    )
    path = args.output / "manifest.json"
    manifest = json.loads(path.read_text(encoding="utf-8"))
    device = copy.deepcopy(next(d for d in manifest["devices"] if d["id"] == "STC8H1K17"))
    device.update(
        id=recipe["id"],
        displayName="STC8H8K64U · 65024 B 代码 / 8 KiB XRAM · 原生 USB / 串口",
        flashBytes=recipe["codeBytes"],
        ramBytes=recipe["ramBytes"],
        defines=["STUDIOX_STC8H", "STC8H8K64U"],
    )
    for option, value in (
        ("--code-size", str(recipe["codeBytes"])),
        ("--xram-size", str(recipe["xramBytes"])),
    ):
        device["linkOptions"][device["linkOptions"].index(option) + 1] = value
    manifest["devices"].append(device)
    manifest["version"] = "0.1.8"
    path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    evidence = {
        "deviceId": "STC8H8K64U",
        "modelCode": "F784",
        "source": "https://stcmicro.com/cn/stc/stc8h8k64u.html",
        "physicalFlashBytes": 65536,
        "codeBytes": 65024,
        "eepromBytes": 512,
        "internalRamBytes": 256,
        "xramBytes": 8192,
        "separateUsbRamBytes": 1280,
        "header": "Existing reviewed STC8H SDCC family header; no pin/board assumptions.",
        "preserved": "All prior devices, Mon51 firmware and RT51 resources remain present.",
        "qualification": "Software profile; native USB hardware qualification tracked separately.",
    }
    (args.output / "vendor/stc8h8k64u.json").write_text(
        json.dumps(evidence, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )
    print("Prepared stc.stc8/0.1.8 with STC8H8K64U.")


if __name__ == "__main__":
    main()
