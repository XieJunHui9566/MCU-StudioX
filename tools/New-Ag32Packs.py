"""从锁定的本机 AGM SDK 生成当前七款 MCU 的四个独立器件包。"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess


PACKS = {
    "AG32VF303": ("studiox.preview.ag32vf303", "0.1.3"),
    "AG32VF407": ("agm.ag32vf407", "0.1.1"),
    "AG32VH303": ("agm.ag32vh303", "0.1.1"),
    "AG32VH407": ("agm.ag32vh407", "0.1.1"),
}
FLASH_ORIGIN = 0x80000000
RAM_ORIGIN = 0x20000000
SDK_INPUTS = (
    "package.json",
    "misc/crt.S",
    "misc/syscalls.c",
    "misc/agrv.h",
    "misc/encoding.h",
    "misc/init.ld",
    "misc/section.ld",
    "misc/devices/AgRV2K_mem.ld",
    "misc/devices/AgRV2K_FLASH.ld",
    "src/interrupt.c",
    "src/AltaRiscv.svd",
)


def digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def write(stage: Path, relative: str, content: str | bytes) -> None:
    destination = stage / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    destination.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))


def json_text(value: object) -> str:
    return json.dumps(value, ensure_ascii=False, indent=2) + "\n"


def read_sources(sdk: Path, platform: Path, catalog: dict) -> tuple[dict[str, bytes], dict]:
    sources = {f"sdk/{name}": (sdk / name).read_bytes() for name in SDK_INPUTS}
    for source in sorted((sdk / "src").glob("*.h")):
        sources["sdk/src/" + source.name] = source.read_bytes()
    if not any(name.startswith("sdk/src/") and name.endswith(".h") for name in sources):
        raise RuntimeError("SDK 没有外设头文件，不能生成器件包。")
    sources["platform/builder/common.py"] = (platform / "builder/common.py").read_bytes()
    sources["platform/etc/gen_vlog"] = (platform / "etc/gen_vlog").read_bytes()
    sources["platform/etc/agrv2k.cfg"] = (platform / "etc/agrv2k.cfg").read_bytes()
    sources["platform/platform.json"] = (platform / "platform.json").read_bytes()
    for board in ("agrv2k_303", "agrv2k_407"):
        sources[f"platform/boards/{board}.json"] = (platform / f"boards/{board}.json").read_bytes()
    for name, expected in catalog["sdk"]["expectedSourceHashes"].items():
        if digest(sources[name]) != expected:
            raise RuntimeError(f"厂商输入发生变化，请先重新审查来源和布局：{name}")
    metadata = json.loads(sources["sdk/package.json"])
    if (
        metadata.get("name") != catalog["sdk"]["packageName"]
        or metadata.get("version") != catalog["sdk"]["packageVersion"]
    ):
        raise RuntimeError("SDK 名称或版本与已核实来源不一致。")

    # 输入清单记录相对来源，不把开发者的 SDK 绝对路径写入包或工程。
    provenance = {
        "source": catalog["sdk"],
        "referenceManual": catalog["referenceManual"],
        "pinout": catalog["pinout"],
        "hyperRamGuide": catalog["hyperRamGuide"],
        "debugProbes": catalog["debugProbes"],
        "sourceFiles": {
            name: {"sha256": digest(data), "bytes": len(data)}
            for name, data in sorted(sources.items())
        },
        "licenseFiles": [],
        "licenseStatus": "本机 SDK 未随附独立 LICENSE；源码版权和许可注释保持原样，不扩大平台的许可声明。",
        "transformations": [
            "保持厂商 C/H/S 原始字节。",
            "展开链接脚本 INCLUDE，将 FLASH_SIZE 限制为物理 Flash 减末尾未压缩逻辑区 100 KiB。",
            "只复制工程编译必需头文件、启动文件、interrupt.c 和 SVD；不复制工具运行时或私人许可。",
        ],
    }
    return sources, provenance


def make_linker(sources: dict[str, bytes], device: dict) -> str:
    memory = sources["sdk/misc/devices/AgRV2K_mem.ld"].decode("utf-8-sig")
    memory, count = re.subn(
        r"PROVIDE\(FLASH_SIZE\s*=\s*16M\);",
        f"PROVIDE(FLASH_SIZE = 0x{device['applicationFlashBytes']:X});",
        memory,
    )
    if count != 1:
        raise RuntimeError("厂商 FLASH_SIZE 布局发生变化，不能猜测替换。")
    flash = sources["sdk/misc/devices/AgRV2K_FLASH.ld"].decode("utf-8-sig")
    flash = re.sub(r"(?m)^INCLUDE\s+[^\r\n]+\r?\n?", "", flash)
    parts = [
        memory,
        flash,
        sources["sdk/misc/init.ld"].decode("utf-8-sig"),
        sources["sdk/misc/section.ld"].decode("utf-8-sig"),
    ]
    return (
        f"/* {device['id']}：应用区 {device['applicationFlashBytes']} B；"
        f"末尾 {device['logicReserveBytes']} B 为未压缩逻辑区。 */\n"
        + "\n".join(parts)
        + "\n"
    )


def make_device(device: dict, stage: Path, verified_target: bytes) -> dict:
    device_id = device["id"]
    linker = f"linker/{device_id.lower()}.ld"
    target = f"debug/{device_id.lower()}.cfg"
    if device_id == "AG32VF303CCT6":
        write(stage, target, verified_target)
    else:
        # 不复用 CCT6 的器件 ID 检查；其它型号需实板身份验证后才允许下载或调试。
        write(
            stage,
            target,
            f'# {device_id}：仅离线工程和引脚配置支持。\n'
            f'error "{device_id}: hardware identity is not verified; '
            'download/debug is disabled"\n',
        )
    return {
        "id": device_id,
        "displayName": (
            f"{device_id} · {device['packageName']} · "
            f"{device['flashBytes'] // 1024} KiB Flash / 128 KiB SRAM"
            + (" + 8 MiB PSRAM" if device["externalPsramBytes"] else "")
        ),
        "architecture": "riscv",
        "flashOrigin": FLASH_ORIGIN,
        "flashBytes": device["flashBytes"],
        "ramOrigin": RAM_ORIGIN,
        "ramBytes": device["ramBytes"],
        "toolsetId": "agm.agrv",
        "toolsetVersion": "1.0.0",
        "compilerId": "agrv-gcc-11.1.0",
        "cpuFlags": ["-march=rv32imafc", "-mabi=ilp32f", "-mcmodel=medlow"],
        "defines": ["FLASH_BOOT", "NDEBUG"],
        "includeDirectories": ["sdk/include", "sdk/startup"],
        "sources": ["sdk/startup/crt.S", "sdk/startup/syscalls.c", "sdk/src/interrupt.c"],
        "linkerScript": linker,
        "compileOptions": [
            "-Os",
            "-g3",
            "-ffunction-sections",
            "-fdata-sections",
            "-fno-common",
            "-mstrict-align",
            "-fsingle-precision-constant",
        ],
        "linkOptions": ["-static", "-nostartfiles", "--specs=nosys.specs", "-Wl,--gc-sections"],
        "openOcd": {
            "targetScript": target,
            "applicationFlashBytes": device["applicationFlashBytes"],
            "probes": [
                {
                    "id": "cmsis-dap",
                    "displayName": "DAP-Link（CMSIS-DAP）",
                    "interfaceScript": "interface/cmsis-dap.cfg",
                    "transport": "swd",
                    "defaultSpeedKhz": 1000,
                },
                {
                    "id": "jlink",
                    "displayName": "J-Link（V9 及以上）",
                    "interfaceScript": "interface/jlink.cfg",
                    "transport": "swd",
                    "defaultSpeedKhz": 1000,
                }
            ],
        },
        "templates": [
            {
                "id": "minimal",
                "displayName": "最小裸机工程 · 内部时钟",
                "description": "内存心跳，不假设板级 LED、串口或晶振。"
                "新工程生成对应封装的 logic/pins.ve；VH 的 PSRAM 需独立 HyperBus 逻辑。",
                "entryFile": "templates/minimal/main.c",
            },
            {
                "id": "editor-demo",
                "displayName": "编辑器演示 · 函数与数据结构",
                "description": "内存中生成数据，可查看注释、函数和数据结构，不依赖板级外设。",
                "entryFile": "templates/editor-demo/main.c",
            },
        ],
    }


def stage_pack(
    series: str,
    devices: list[dict],
    output: Path,
    recipe: Path,
    sdk: Path,
    sources: dict[str, bytes],
    provenance: dict,
) -> tuple[Path, dict]:
    pack_id, version = PACKS[series]
    stage = output / "source" / pack_id
    stage.mkdir(parents=True, exist_ok=False)
    for name, data in sources.items():
        if name.startswith("sdk/src/") and name.endswith(".h"):
            write(stage, "sdk/include/" + Path(name).name, data)
    for name in ("crt.S", "syscalls.c", "agrv.h", "encoding.h"):
        write(stage, "sdk/startup/" + name, sources["sdk/misc/" + name])
    write(stage, "sdk/src/interrupt.c", sources["sdk/src/interrupt.c"])
    write(stage, "svd/AltaRiscv.svd", sources["sdk/src/AltaRiscv.svd"])
    write(stage, "vendor/framework-agrv_sdk.json", sources["sdk/package.json"])
    write(stage, "templates/minimal/main.c", (recipe / "main.c").read_bytes())
    preview = recipe.parent / "agm.ag32vf303-preview"
    write(
        stage,
        "templates/editor-demo/main.c",
        (preview / "templates/editor-demo/main.c").read_bytes(),
    )
    write(stage, "README.md", (recipe / "README.md").read_bytes())

    details = dict(provenance)
    details["devices"] = devices
    details["licenseFiles"] = []
    for relative in ("LICENSE", "LICENSE.txt", "LICENSE.md", "COPYING", "NOTICE"):
        license_file = sdk / relative
        if license_file.is_file():
            content = license_file.read_bytes()
            write(stage, "vendor/licenses/" + relative, content)
            details["licenseFiles"].append({"path": relative, "sha256": digest(content)})
    if details["licenseFiles"]:
        details["licenseStatus"] = "保留 SDK 随附许可文件和源码版权注释；不扩大许可范围。"
    write(stage, "vendor/provenance.json", json_text(details))
    write(
        stage,
        "vendor/NOTICE.md",
        "# 厂商源码与来源\n\n" + details["licenseStatus"]
        + "\n\n完整来源版本、文件 SHA-256、布局转换及器件事实见 provenance.json。\n",
    )
    verified_target = (preview / "debug/ag32vf303.cfg").read_bytes()
    definitions = []
    for device in devices:
        write(stage, f"linker/{device['id'].lower()}.ld", make_linker(sources, device))
        definitions.append(make_device(device, stage, verified_target))
    manifest = {
        "formatVersion": 1,
        "id": pack_id,
        "version": version,
        "displayName": f"{series} · MCU 引脚映射"
        + (" / 8 MiB PSRAM" if "VH" in series else ""),
        "vendor": "AGM",
        "devices": definitions,
    }
    write(stage, "manifest.json", json_text(manifest))
    return stage, manifest


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdk-directory", required=True, type=Path)
    parser.add_argument("--platform-directory", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path, help="不存在的新输出目录")
    parser.add_argument("--cli", type=Path, help="已编译 StudioX.Cli.dll；打包时必须显式提供")
    parser.add_argument("--stage-only", action="store_true")
    args = parser.parse_args()
    if not args.stage_only and (args.cli is None or not args.cli.is_file()):
        parser.error("打包需要 --cli 指向已有 StudioX.Cli.dll；可用 --stage-only 仅生成源码。")
    repo = Path(__file__).resolve().parents[1]
    recipe = repo / "examples/packs/agm.ag32-series"
    catalog = json.loads((recipe / "devices.json").read_text(encoding="utf-8"))
    devices = catalog["devices"]
    if len(devices) != 7 or len({device["id"] for device in devices}) != 7:
        raise RuntimeError("当前核实的订货表必须有七个精确 MCU 型号。")
    for device in devices:
        if (
            device["applicationFlashBytes"] + device["logicReserveBytes"] != device["flashBytes"]
            or device["logicImageAddress"] != FLASH_ORIGIN + device["applicationFlashBytes"]
        ):
            raise RuntimeError("器件布局与厂商 SDK 未压缩逻辑保留区不一致：" + device["id"])
    sdk = args.sdk_directory.resolve()
    platform = args.platform_directory.resolve()
    sources, provenance = read_sources(sdk, platform, catalog)
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    index = []
    for series in PACKS:
        selected = [device for device in devices if device["series"] == series]
        stage, manifest = stage_pack(series, selected, output, recipe, sdk, sources, provenance)
        entry = {
            "id": manifest["id"],
            "version": manifest["version"],
            "sourceDirectory": stage.relative_to(output).as_posix(),
            "devices": [device["id"] for device in selected],
        }
        if not args.stage_only:
            archive = output / f"{manifest['id']}-{manifest['version']}.mcupack"
            # 工具原始输出直接交给终端；失败停止，不吞掉 pack validator 的诊断。
            subprocess.run(
                ["dotnet", str(args.cli.resolve()), "pack", str(stage), str(archive)],
                check=True,
                cwd=repo,
            )
            entry.update(
                {
                    "file": archive.name,
                    "sha256": digest(archive.read_bytes()),
                    "bytes": archive.stat().st_size,
                }
            )
        index.append(entry)
        if args.stage_only:
            print(stage)
    write(output, "index.json", json_text(index))


if __name__ == "__main__":
    main()
