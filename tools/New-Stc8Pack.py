"""Create the single StudioX STC 8-bit pack from verified local AiCube facts."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess


PACK_ID = "stc.stc8"
PACK_VERSION = "0.1.0"
SOURCE_DIRECTORY = Path("AiCube_sources_and_firmware_extracted/AiCube_source_material/res33_split")
# 解包器将源头文件从 GB18030 解码为 UTF-8；sha256 核对解码后的文件。
HEADERS = {
    "stc89": {"slot": 13, "file": "slot_00013__off_00006c6e__len_0005270.h", "sha256": "d4c4410af55097a770b837484b8b16246e0b97fa42c8fc506ef8b8580b7cbfb3", "rawSha256": "de24e95db01c6eaf20c2a347e7e30a0a9eadf1ac6b92c0b8adf99b8a35eef0cb", "counts": (41, 104, 0), "datasheet": "https://www.stcmicro.com/datasheet/STC89C51RC-en.pdf"},
    "stc12": {"slot": 63, "file": "slot_00063__off_0002e40d__len_0006205.h", "sha256": "fa7882a376819bc1f71b0a2e88ce6a2d75583c2bd8d1a7c8c4c644e0cd9db111", "rawSha256": "12de2e4cfad725ed14c53520a5a0146cd5aae15c31f7ba047fd90c70225a8b6a", "counts": (76, 96, 0), "datasheet": "https://www.stcmicro.com/datasheet/STC12C5A60S2-en.pdf"},
    "stc15": {"slot": 83, "file": "slot_00083__off_000455f8__len_0010754.h", "sha256": "d818bef2e23c6911cde6c1e3cc0f8d5f1471e4c6d3e8f89a0f89caf3397b22ef", "rawSha256": "ab9ba4c249fed351db8198085197556403cf4bf3f514421482b18e07a9b8ff26", "counts": (110, 117, 46), "datasheet": "https://www.stcmicro.com/datasheet/STC15F2K60S2-en.pdf"},
    "stc8g": {"slot": 103, "file": "slot_00103__off_0008966e__len_0046884.h", "sha256": "d41bacd42d67a10a6e7f43396127b04295776831b89fce205cdebd4b41f4ea6b", "rawSha256": "2da06e811b3b638afd9e2ad56e05272a3f5d56eda391ce9f3f0631af5e89ba4b", "counts": (121, 117, 678), "datasheet": "https://www.stcmicro.com/stc/stc8g1k08.html"},
    "stc8h": {"slot": 113, "file": "slot_00113__off_000ce4fd__len_0068135.h", "sha256": "7bb20b68c12be27c8cc58879fae03c0e2d73610b2d1df27ea32fa6d03493a49e", "rawSha256": "66ac13127b1c83f231257437eb56e3cdfcb043cc20526770b533b0a81861bb3c", "counts": (110, 111, 754), "datasheet": "https://www.stcmicro.com/stc/stc8h1k08.html"},
}
DECLARATION = re.compile(
    r"(?m)^\s*__(sfr|sbit)\s+__at\(0x([0-9a-fA-F]+)\)\s+([A-Za-z_]\w*)\s*;\s*$"
)
XDATA_DECLARATION = re.compile(
    r"(?m)^\s*__xdata\s+volatile\s+unsigned\s+(char|short)\s+"
    r"__at\(0x([0-9a-fA-F]+)\)\s+([A-Za-z_]\w*)\s*;\s*$"
)


def sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def make_header(source: str, family: str, expected: tuple[int, int, int]) -> tuple[bytes, dict[str, int]]:
    # SFR 名称及地址是器件事实。按地址/名称重新排序并规范化格式，不复制原文件注释。
    declarations: dict[str, tuple[str, int]] = {}
    for kind, address, name in DECLARATION.findall(source):
        value = (kind, int(address, 16))
        if name in declarations and declarations[name] != value:
            raise RuntimeError(f"Conflicting AiCube register declaration: {name}")
        declarations[name] = value
    sfr_count = sum(kind == "sfr" for kind, _ in declarations.values())
    sbit_count = sum(kind == "sbit" for kind, _ in declarations.values())

    xdata: dict[str, tuple[str, int]] = {}
    for width, address, name in XDATA_DECLARATION.findall(source):
        value = (width, int(address, 16))
        if name in xdata and xdata[name] != value:
            raise RuntimeError(f"Conflicting AiCube extended register declaration: {name}")
        xdata[name] = value
    if (sfr_count, sbit_count, len(xdata)) != expected:
        raise RuntimeError(f"{family} SDCC register table changed; review the source before packaging")
    if set(declarations) & set(xdata):
        raise RuntimeError(f"{family} has conflicting SFR and XDATA register names")

    guard = "STUDIOX_" + family.upper() + "_REGISTERS_H"
    lines = [
        f"/* StudioX {family.upper()} SDCC register declarations. See vendor/provenance.json. */",
        f"#ifndef {guard}",
        f"#define {guard}",
        "",
    ]
    for kind in ("sfr", "sbit"):
        lines.append("/* " + ("Special function registers." if kind == "sfr" else "Bit-addressable registers.") + " */")
        for name, (found_kind, address) in sorted(declarations.items(), key=lambda item: (item[1][1], item[0])):
            if found_kind == kind:
                lines.append(f"__{kind} __at(0x{address:02X}) {name};")
        lines.append("")
    lines.append("/* Extended peripheral registers; P_SW2 bit 7 controls access on this family. */")
    for name, (width, address) in sorted(xdata.items(), key=lambda item: (item[1][1], item[0])):
        lines.append(f"#define {name} (*((volatile __xdata unsigned {width} *)0x{address:04X}))")
    lines += ["", "#endif", ""]
    return "\n".join(lines).encode("ascii"), {"sfr": sfr_count, "sbit": sbit_count, "xdata": len(xdata)}


def write(stage: Path, name: str, content: bytes | str) -> None:
    destination = stage / name
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--aicube-root", required=True, type=Path, help="Directory containing AiCube_sources_and_firmware_extracted")
    parser.add_argument("--output", required=True, type=Path, help="New output directory")
    parser.add_argument("--stage-only", action="store_true", help="Prepare source without invoking StudioX pack CLI")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    recipe = repo / "examples/packs/stc.stc8"
    devices = json.loads((recipe / "devices.json").read_text(encoding="utf-8"))
    if len(devices) != 24 or len({device["id"] for device in devices}) != 24:
        raise RuntimeError("Expected 24 distinct official STC 8-bit models")

    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    stage = output / "source"
    source_template = (recipe / "main.c").read_text(encoding="utf-8")
    provenance_headers = {}
    for family, entry in HEADERS.items():
        source_relative = SOURCE_DIRECTORY / entry["file"]
        source_bytes = (args.aicube_root / source_relative).read_bytes()
        if sha256(source_bytes) != entry["sha256"]:
            raise RuntimeError(f"AiCube {family} SDCC header changed: {source_relative}")
        generated_header, counts = make_header(source_bytes.decode("utf-8"), family, entry["counts"])
        write(stage, f"sdk/include/{family}.h", generated_header)
        write(stage, f"templates/{family}/main.c", source_template.replace("stc15.h", family + ".h"))
        provenance_headers[family] = {
            "sourceRelativePath": source_relative.as_posix(),
            "decodedSourceSha256": entry["sha256"],
            "originalBlobSha256": entry["rawSha256"],
            "generatedHeaderSha256": sha256(generated_header),
            "registerCounts": counts,
            "datasheet": entry["datasheet"],
        }
    write(stage, "README.md", (recipe / "README.md").read_bytes())
    write(stage, "vendor/devices.json", (recipe / "devices.json").read_bytes())
    provenance = {
        "source": "AiCube-ISP-v6.96V-plus.exe local static extraction, res33 SDCC variants",
        "headers": provenance_headers,
        "licenseStatus": "No explicit redistribution license found in local AiCube extraction; original header is not bundled.",
        "transformation": "Only register names, addresses and widths were used; declarations were normalized and sorted, original comments/examples omitted.",
        "deviceCapacitySource": "Official STC data sheet device tables and SRAM chapters. STC15 code size reserves seven Global ID bytes.",
    }
    write(stage, "vendor/provenance.json", json.dumps(provenance, ensure_ascii=False, indent=2) + "\n")

    definitions = []
    for device in devices:
        device_id = device["id"]
        family = device.get("family", "stc15")
        if family not in HEADERS:
            raise RuntimeError(f"No pinned SDCC header for {device_id}")
        flash_bytes = device["flashKib"] * 1024
        ram_bytes = device.get("ramBytes", 2048)
        xram_bytes = device.get("xramBytes", 1792)
        if ram_bytes != 256 + xram_bytes:
            raise RuntimeError(f"Incorrect RAM split for {device_id}")
        definitions.append({
            "id": device_id,
            "displayName": f"{device_id} · {device['flashKib']} KiB Flash / {ram_bytes} B SRAM · {device['voltage']}",
            "architecture": "mcs51",
            "flashOrigin": 0,
            "flashBytes": flash_bytes,
            "ramOrigin": 0,
            "ramBytes": ram_bytes,
            "toolsetId": "stc.sdcc",
            "toolsetVersion": "1.0.0",
            "compilerId": "sdcc-4.5.0-15242",
            "cpuFlags": ["-mmcs51", "--model-large"],
            "defines": ["STUDIOX_" + family.upper(), device_id],
            "includeDirectories": ["sdk/include"],
            "sources": [],
            "linkerScript": "",
            "compileOptions": [],
            "linkOptions": ["--code-size", str(flash_bytes - (7 if family == "stc15" else 0)), "--iram-size", "256", "--xram-loc", "0", "--xram-size", str(xram_bytes)],
            "templates": [{
                "id": "bare",
                "displayName": "SDCC 裸机 C · 无板级引脚假设",
                "description": f"{family.upper()}；只生成 C 入口和片内 XRAM 计数，不配置引脚或调试。",
                "entryFile": f"templates/{family}/main.c",
            }],
        })
    manifest = {
        "formatVersion": 1,
        "id": PACK_ID,
        "version": PACK_VERSION,
        "displayName": "STC 8 位 · SDCC（STC89/12/15/8G/8H）",
        "vendor": "STC / 宏晶科技",
        "devices": definitions,
    }
    write(stage, "manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    if args.stage_only:
        print(stage)
        return

    archive = output / f"{PACK_ID}-{PACK_VERSION}.mcupack"
    subprocess.run([
        "dotnet", "run", "--project", str(repo / "src/StudioX.Cli/StudioX.Cli.csproj"),
        "-c", "Release", "--", "pack", str(stage), str(archive),
    ], check=True, cwd=repo)
    index = [{
        "file": archive.name,
        "id": PACK_ID,
        "version": PACK_VERSION,
        "devices": [device["id"] for device in definitions],
        "sha256": sha256(archive.read_bytes()),
    }]
    (output / "index.json").write_text(json.dumps(index, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(archive)


if __name__ == "__main__":
    main()
