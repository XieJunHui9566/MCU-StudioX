"""Prepare the exact IAP15L2K61S2/LQFP44 configuration preview data and pack source."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import zipfile

from StcConfiguratorPeripherals import extend


DEVICE = "IAP15L2K61S2"
PACK = "stc.iap15l2k61s2.config-preview"
PLUGIN = "studiox.stc15-configurator"
PDF_SHA = "c1df3cdb156465acc3f1d07b283b0c599d33cc170a7946a8cb9330aa84273801"
HEADER_SHA = "d818bef2e23c6911cde6c1e3cc0f8d5f1471e4c6d3e8f89a0f89caf3397b22ef"
ISP_SHA = "d857b147f2f4e527c5566a2dd40e63f875c94f4e344fa5ce800ab13a770db420"
IRC_PRESETS_HZ = [
    5529600,
    6000000,
    11059200,
    12000000,
    18432000,
    20000000,
    22118400,
    24000000,
    27000000,
]
PAD_NAMES = [
    "P0.5",
    "P0.6",
    "P0.7",
    "P1.0",
    "P1.1",
    "P1.2",
    "P1.3",
    "P1.4",
    "P1.5",
    "P1.6",
    "P4.7",
    "P1.7",
    "P5.4",
    "VCC",
    "P5.5",
    "GND",
    "P4.0",
    "P3.0",
    "P3.1",
    "P3.2",
    "P3.3",
    "P3.4",
    "P3.5",
    "P3.6",
    "P3.7",
    "P4.1",
    "P4.2",
    "P4.3",
    "P4.4",
    "P2.0",
    "P2.1",
    "P2.2",
    "P2.3",
    "P2.4",
    "P2.5",
    "P2.6",
    "P2.7",
    "P4.5",
    "P4.6",
    "P0.0",
    "P0.1",
    "P0.2",
    "P0.3",
    "P0.4",
]
UART = {
    "P3.0": "RX-default",
    "P3.1": "TX-default",
    "P3.6": "RX-p36",
    "P3.7": "TX-p36",
    "P1.6": "RX-p16",
    "P1.7": "TX-p16",
}


def sha(raw: bytes) -> str:
    return hashlib.sha256(raw).hexdigest()


def write(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--base-pack", type=Path, required=True)
    parser.add_argument("--datasheet", type=Path, required=True)
    parser.add_argument("--aicube-header", type=Path, required=True)
    parser.add_argument("--stc-isp", type=Path, required=True)
    parser.add_argument("--assets", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise ValueError("Use a new output directory.")
    if (
        sha(args.datasheet.read_bytes()) != PDF_SHA
        or sha(args.aicube_header.read_bytes()) != HEADER_SHA
        or sha(args.stc_isp.read_bytes()) != ISP_SHA
    ):
        raise ValueError("Evidence bytes changed; review the exact source before generating.")
    with zipfile.ZipFile(args.base_pack) as archive:
        manifest = json.loads(archive.read("manifest.json"))
        if manifest["id"] != "stc.stc8" or manifest["version"] != "0.1.7":
            raise ValueError("The reviewed base pack must be stc.stc8/0.1.7.")
        hashes = json.loads(archive.read("files.sha256.json"))
        names = set(archive.namelist())
        if names != set(hashes) | {"files.sha256.json"}:
            raise ValueError("The base archive file set differs from its index.")
        for name, expected in hashes.items():
            relative = Path(name)
            if (
                relative.is_absolute()
                or ".." in relative.parts
                or "\\" in name
                or ":" in name
                or sha(archive.read(name)).lower() != expected.lower()
            ):
                raise ValueError("Invalid indexed base resource: " + name)
        target = next(device for device in manifest["devices"] if device["id"] == DEVICE)
        target.pop("monitorFirmware", None)
        target["displayName"] = "IAP15L2K61S2 · LQFP44 · 61 KiB Flash / 2 KiB RAM"
        template = next(item for item in target["templates"] if item["id"] == "bare")
        template["displayName"] = "裸机 C · LQFP44 图形配置预览"
        template["description"] = "可选插件配置；未安装插件时仍是普通 SDCC C 工程。"
        template["graphicalConfiguration"] = {"file": "templates/graphical/device.mcuconfig"}
        target["templates"] = [template]
        for name in sorted(names - {"manifest.json", "files.sha256.json"}):
            if name.startswith("debug/"):
                continue
            path = args.output / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(archive.read(name))
    peripherals = []
    for port in range(5):
        peripherals.append(
            {
                "id": f"GPIO{port}",
                "kind": "gpio",
                "displayName": f"P{port} GPIO",
                "signals": [
                    {
                        "id": f"P{port}.{bit}",
                        "displayName": f"P{port}.{bit}",
                        "direction": "bidirectional",
                        "profileData": {},
                    }
                    for bit in range(8)
                ],
                "evidenceIds": ["datasheet", "registers"],
                "profileData": {},
            }
        )
    peripherals += [
        {
            "id": "UART1",
            "kind": "uart",
            "displayName": "UART1",
            "signals": [
                {
                    "id": value,
                    "displayName": value,
                    "direction": "input" if value.startswith("RX") else "output",
                    "profileData": {},
                }
                for value in UART.values()
            ],
            "evidenceIds": ["datasheet", "registers"],
            "profileData": {},
        },
        *[
            {
                "id": f"TIMER{index}",
                "kind": "timer",
                "displayName": f"Timer {index}",
                "signals": [],
                "evidenceIds": ["datasheet", "registers"],
                "profileData": {},
            }
            for index in range(2)
        ],
    ]
    pins = []
    for index, name in enumerate(PAD_NAMES):
        edge, offset = divmod(index, 11)
        location = 60 + offset * 88
        x, y, side = [
            (location, 1000, "bottom"),
            (1000, 1000 - location, "right"),
            (1000 - location, 0, "top"),
            (0, location, "left"),
        ][edge]
        functions = []
        if name.startswith("P") and int(name[1]) < 5:
            functions.append(
                {
                    "id": "gpio",
                    "displayName": "GPIO",
                    "peripheralId": "GPIO" + name[1],
                    "signalId": name,
                    "profileData": {},
                }
            )
        if name in UART:
            functions.append(
                {
                    "id": "uart1",
                    "displayName": "UART1 " + UART[name],
                    "peripheralId": "UART1",
                    "signalId": UART[name],
                    "profileData": {},
                }
            )
        pins.append(
            {
                "id": f"pad-{index+1}",
                "position": str(index + 1),
                "displayName": name,
                "category": (
                    "supply"
                    if name == "VCC"
                    else (
                        "ground"
                        if name == "GND"
                        else ("reset" if name == "P5.4" else "reserved" if name == "P5.5" else "io")
                    )
                ),
                "point": {"x": x, "y": y},
                "side": side,
                "functions": functions,
                "evidenceIds": ["datasheet"],
                "profileData": {},
            }
        )
    schema = {
        "$schema": "https://json-schema.org/draft/2020-12/schema",
        "$id": "urn:studiox:stc15:config-preview:3.0",
        "type": "object",
        "required": ["target", "profile", "configuration"],
        "properties": {
            "target": {
                "const": {
                    "vendorId": "STC",
                    "deviceId": DEVICE,
                    "packageId": "LQFP44",
                    "architecture": "mcs51",
                }
            },
            "configuration": {
                "type": "object",
                "required": ["pins", "peripherals", "clocks", "profileData"],
                "properties": {
                    "profileData": {
                        "type": "object",
                        "additionalProperties": False,
                        "required": ["reserveMon51", "projectClock", "p54GpioConfirmed"],
                        "properties": {
                            "reserveMon51": {"type": "boolean"},
                            "p54GpioConfirmed": {"type": "boolean"},
                            "projectClock": {
                                "type": "object",
                                "additionalProperties": False,
                                "required": ["managed", "valid", "inputSha256"],
                                "properties": {
                                    "managed": {"type": "boolean"},
                                    "valid": {"type": "boolean"},
                                    "inputSha256": {
                                        "type": ["string", "null"],
                                        "pattern": "^[a-f0-9]{64}$",
                                    },
                                },
                            },
                        },
                    }
                },
            },
        },
        "description": "预览版的 profile 叠加结构；完整参数和资源校验由插件执行，不宣称完整 mcuConfigurationV1。",
    }
    write(args.assets / "stc15-profile.schema.json", schema)
    clock_nodes = []
    for node_id, kind, inputs, x, y in [
        ("IRC", "source", [], 0, 0),
        ("XTAL", "source", [], 0, 170),
        ("SYS", "divider", ["IRC", "XTAL"], 240, 80),
        ("T0", "divider", ["SYS"], 480, 0),
        ("T1", "divider", ["SYS"], 480, 160),
        ("T2", "divider", ["SYS"], 720, 80),
    ]:
        clock_nodes.append(
            {
                "id": node_id,
                "kind": kind,
                "displayName": node_id,
                "inputIds": inputs,
                "point": {"x": x, "y": y},
                "evidenceIds": ["datasheet", "registers"],
                "profileData": {},
            }
        )
    document = {
        "format": "studiox.mcuconfig",
        "formatVersion": 1,
        "documentKind": "template",
        "target": {
            "vendorId": "STC",
            "deviceId": DEVICE,
            "packageId": "LQFP44",
            "architecture": "mcs51",
        },
        "profile": {
            "id": "stc.iap15l2k61s2.lqfp44.sdcc-preview",
            "version": "2.2.0",
            "pluginId": PLUGIN,
            "providerId": "stc15-lqfp44-preview",
            "dataSchema": {
                "id": "stc15.preview.data",
                "version": "2.2.0",
                "sha256": sha((args.assets / "stc15-profile.schema.json").read_bytes()),
            },
            "framework": {"id": "bare-metal-sdcc", "version": "4.5.0"},
            "sdk": None,
        },
        "origin": None,
        "definitions": {
            "package": {
                "id": "LQFP44",
                "displayName": "LQFP44 · 顶视图",
                "kind": "LQFP",
                "pinCount": 44,
                "width": 1000,
                "height": 1000,
            },
            "pins": pins,
            "peripherals": peripherals,
            "clockTree": {"nodes": clock_nodes},
            "evidence": [
                {
                    "id": "datasheet",
                    "kind": "datasheet",
                    "title": "STC15 series MCU Data Sheet",
                    "version": "2015-10-10",
                    "locator": "https://www.stcmicro.com/datasheet/STC15F2K60S2-en.pdf",
                    "sha256": PDF_SHA,
                    "section": "p19 LQFP44 pinout; p21-22 CLK_DIV, system clock; Timer 0/1/2 and UART1 clock chapters",
                },
                {
                    "id": "registers",
                    "kind": "devicePack",
                    "title": "AiCube 6.96V-plus STC15 SDCC register facts",
                    "version": "6.96V-plus",
                    "locator": "res33_split/slot_00083__off_000455f8__len_0010754.h",
                    "sha256": HEADER_SHA,
                    "section": "SFR names and addresses; original vendor source is not redistributed",
                },
                {
                    "id": "irc-presets",
                    "kind": "sdk",
                    "title": "STC-ISP IAP15L2K61S2 IRC frequency combo box",
                    "version": "6.97C",
                    "locator": "AiCube-ISP-v6.97C.exe / IAP15L2K61S2 / control 1045",
                    "sha256": ISP_SHA,
                    "section": "Nine preset items observed in the UI; editable input is also supported, presets are not a hardware frequency enumeration",
                },
            ],
            "profileData": {
                "previewOnly": False,
                "codeGeneration": {
                    "contractVersion": 1,
                    "mainPath": "src/main.c",
                    "entryHeader": "generated/mcuconfig/mcuconfig.h",
                    "entryFunction": "sx_mcuconfig_init",
                },
                "ircFrequencyPresetsHz": IRC_PRESETS_HZ,
                "projectClock": {
                    "contractVersion": 1,
                    "systemNodeId": "SYS",
                    "sourceKinds": {"IRC": "internalRc", "XTAL": "externalCrystal"},
                },
            },
        },
        "configuration": {
            "pins": [],
            "peripherals": [],
            "clocks": [
                {
                    "nodeId": "SYS",
                    "enabled": True,
                    "inputId": None,
                    "parameters": {"divider": 1},
                }
            ],
            "profileData": {
                "reserveMon51": True,
                "projectClock": {"managed": False, "valid": False, "inputSha256": None},
            },
        },
        "generation": None,
    }
    extend(document)
    write(args.assets / "iap15l2k61s2-lqfp44.mcuconfig", document)
    write(args.output / "templates/graphical/device.mcuconfig", document)
    definitions_hash = sha(
        json.dumps(
            document["definitions"],
            ensure_ascii=False,
            sort_keys=True,
            separators=(",", ":"),
        ).encode("utf-8")
    )
    write(
        args.assets / "provenance.json",
        {
            "definitionsSha256": definitions_hash,
            "basePackSha256": sha(args.base_pack.read_bytes()),
            "datasheetSha256": PDF_SHA,
            "aicubeHeaderSha256": HEADER_SHA,
            "stcIspSha256": ISP_SHA,
            "ircFrequencyPresetsHz": IRC_PRESETS_HZ,
            "status": "Offline preview; no hardware connection; exact LQFP44 only",
            "notice": "Vendor facts re-expressed independently. No vendor program, PDF or original source included.",
        },
    )
    manifest.update(
        {
            "id": PACK,
            "version": "0.1.4",
            "displayName": "IAP15L2K61S2 · LQFP44 图形配置预览",
            "devices": [target],
        }
    )
    # SDCC 的启动钩子在 main 前运行；模板持有寄存器规则，宿主只传入声明的分频数。
    startup = [
        '#include "stc15.h"',
        "",
        "/* 来自已保存时钟树；保留 CLK_DIV 的输出/ADC/转发位。 */",
        "unsigned char __sdcc_external_startup(void)",
        "{",
    ]
    for exponent in range(8):
        startup += [
            ("#if" if exponent == 0 else "#elif")
            + " defined(STUDIOX_CLOCK_DIVIDER) && STUDIOX_CLOCK_DIVIDER == "
            + str(1 << exponent),
            "    CLK_DIV = (CLK_DIV & 0xF8) | " + str(exponent) + ";",
        ]
    startup += [
        "#elif defined(STUDIOX_CLOCK_DIVIDER)",
        "#error Unsupported_system_clock_divider",
        "#endif",
        "    return 0; /* 继续 SDCC 标准 RAM 初始化。 */",
        "}",
        "",
    ]
    startup_path = "sdk/support/stc15_clock_startup.c"
    (args.output / startup_path).parent.mkdir(parents=True, exist_ok=True)
    (args.output / startup_path).write_text("\n".join(startup), encoding="utf-8", newline="\n")
    target["sources"] = [*target.get("sources", []), startup_path]
    # 模板接入点在生成目录之外，首次生成前后都可链接；不扫描任意 main 函数插入代码。
    main_path = args.output / "templates/stc15/main.c"
    main_text = main_path.read_text(encoding="utf-8-sig")
    main_text = main_text.replace(
        '#include "stc15.h"',
        '#include "stc15.h"\n/* StudioX MCU includes begin */\n/* StudioX MCU includes end */',
    )
    main_text = main_text.replace(
        "void main(void)\n{",
        "void main(void)\n{\n    /* StudioX MCU init begin */\n    /* StudioX MCU init end */",
    )
    if main_text.count("/* StudioX MCU init begin */") != 1:
        raise ValueError("The reviewed main template changed.")
    main_path.write_text(main_text, encoding="utf-8", newline="\n")
    write(args.output / "manifest.json", manifest)
    print(
        json.dumps(
            {
                "packSource": str(args.output),
                "assets": str(args.assets),
                "definitionsSha256": definitions_hash,
            }
        )
    )


if __name__ == "__main__":
    main()
