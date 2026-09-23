"""Build a pinned StudioX CH595 pack from local, official MounRiver SDK files."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile


PACK_ID = "wch.ch595"
VERSION = "0.1.0"
ARCHIVES = {
    "D": "365d04c939488d4e24f3598e539d5fbfca8df0902609592e71bc34c232ea7719",
    "F": "fb508199d0c51b98ae12a2edd6e493f7924cb7fff1d4b92bb8efc8cb39c75f52",
    "X": "d653dd85750ef50aee0196f4e1ca2c735a5c5a9e016162fb6cb99c13e71859c4",
}
METADATA = {
    "CH595x.svd": "634f07dda97ed205de71766ad94044892bab8d3da6cef46c30d1a4b2f011af50",
    "CH595-targetProcessor.json": "2d782e80f96e33378098f58cbfaa49fc6edf7f9c5bc4984d1cf37f47afe78e4d",
    "CH595-flash.json": "9c07b5c86d5126b954dd4378d7b6bddd1020ace631632fb702b183ee610cffee",
}
SDK_PREFIXES = ("RVMSIS/", "Startup/", "StdPeriphDriver/", "Ld/")


def checked_bytes(path: Path, expected: str) -> bytes:
    content = path.read_bytes()
    digest = hashlib.sha256(content).hexdigest()
    if digest != expected:
        raise RuntimeError(f"Official SDK file changed; review before updating recipe: {path.name}: {digest}")
    return content


def main() -> None:
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--sdk", type=Path, required=True, help="MounRiver CH595/NoneOS directory")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    recipe = repo / "examples/packs/wch.ch595"
    cli = repo / "src/StudioX.Cli/bin/Release/net10.0/StudioX.Cli.dll"
    if not cli.is_file():
        raise RuntimeError("Build StudioX.Cli in Release before packaging")
    sdk = args.sdk.resolve(strict=True)
    metadata = {name: checked_bytes(sdk / name, sha) for name, sha in METADATA.items()}
    processor = json.loads(metadata["CH595-targetProcessor.json"])
    flash = json.loads(metadata["CH595-flash.json"])
    if processor["architecture"] != "rv32i" or processor["integer_ABI"] != "ilp32" or flash["type"] != "CH595/6" or flash["id"] != 203:
        raise RuntimeError("CH595 SDK target metadata changed")

    archives: dict[str, zipfile.ZipFile] = {}
    try:
        for suffix, digest in ARCHIVES.items():
            archive = zipfile.ZipFile(sdk / f"CH595{suffix}.zip")
            if hashlib.sha256((sdk / f"CH595{suffix}.zip").read_bytes()).hexdigest() != digest:
                archive.close()
                raise RuntimeError(f"CH595{suffix}.zip changed; review the SDK before packaging")
            description = archive.read(".template").decode("utf-8", errors="replace")
            if not re.search(rf"(?m)^MCU=CH595{suffix}$", description):
                archive.close()
                raise RuntimeError(f"CH595{suffix}.zip identifies a different device")
            linker = archive.read("Ld/Link.ld").decode("ascii", errors="replace")
            if not re.search(r"FLASH\s*\(rx\)\s*:\s*ORIGIN\s*=\s*0x00000000\s*,\s*LENGTH\s*=\s*240K", linker) or not re.search(
                r"RAM\s*\(xrw\)\s*:\s*ORIGIN\s*=\s*0x20000000\s*,\s*LENGTH\s*=\s*32K", linker
            ):
                archive.close()
                raise RuntimeError(f"CH595{suffix}.zip linker memory range changed")
            archives[suffix] = archive

        # D/F/X differ only in Eclipse metadata, not the firmware support files.
        # Fail closed if a later official SDK makes the source package specific.
        sdk_files = [name for name in archives["D"].namelist() if not name.endswith("/") and name.startswith(SDK_PREFIXES)]
        for suffix in "FX":
            if any(name not in archives[suffix].namelist() or archives[suffix].read(name) != archives["D"].read(name) for name in sdk_files):
                raise RuntimeError(f"CH595{suffix}.zip firmware support differs from CH595D.zip; review per-device assets")
        if not sdk_files or "Startup/startup_CH595.S" not in sdk_files or "Ld/Link.ld" not in sdk_files:
            raise RuntimeError("CH595 SDK support files are incomplete")

        output = args.output.resolve()
        output.mkdir(parents=True, exist_ok=False)
        stage = output / "source"

        def write(name: str, content: bytes | str) -> None:
            target = stage / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))

        for name in sdk_files:
            write("sdk/" + name, archives["D"].read(name))
        write("svd/CH595x.svd", metadata["CH595x.svd"])
        write("vendor/CH595-targetProcessor.json", metadata["CH595-targetProcessor.json"])
        write("vendor/CH595-flash.json", metadata["CH595-flash.json"])
        write("templates/main.c", (recipe / "main.c").read_bytes())
        write("interface/wch-link.cfg", (recipe / "wch-link.cfg").read_bytes())
        write("README.md", (recipe / "README.md").read_bytes())

        sources = sorted("sdk/" + name for name in sdk_files if name.startswith("StdPeriphDriver/") and name.endswith(".c"))
        sources.append("sdk/Startup/startup_CH595.S")
        devices = []
        for suffix in "DFX":
            device_id = "CH595" + suffix
            target_path = "debug/" + device_id.lower() + ".cfg"
            target = (recipe / "ch595.cfg.in").read_text(encoding="utf-8").replace("@DEVICE_ID@", device_id)
            write(target_path, target)
            devices.append(dict(
                id=device_id,
                displayName=device_id,
                architecture="riscv",
                flashOrigin=0,
                flashBytes=256 * 1024,
                ramOrigin=0x20000000,
                ramBytes=32 * 1024,
                toolsetId="wch.riscv",
                toolsetVersion="1.0.0",
                compilerId="wch-gcc-12.2.0-v1.4",
                # 官方 .cproject 配置 M/C/B/XW 而 A=false；GCC12 须明确展开 B 子扩展。
                cpuFlags=["-march=rv32imc_zba_zbb_zbc_zbs_xw", "-mabi=ilp32", "-msmall-data-limit=8"],
                defines=["FREQ_SYS=80000000", "SYSCLK_FREQ=CLK_SOURCE_HSI_PLL_80MHz"],
                includeDirectories=["sdk/RVMSIS", "sdk/StdPeriphDriver/inc"],
                sources=sources,
                linkerScript="sdk/Ld/Link.ld",
                compileOptions=["-Og", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common", "-fsigned-char"],
                linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections", "-Wl,--print-memory-usage"],
                templates=[dict(id="spl", displayName="标准库 · 精简 main", entryFile="templates/main.c",
                                description="内部 HSI + PLL 80 MHz；256 KiB 物理 ROM，应用代码限定为 240 KiB；32 KiB SRAM。模板不使用板级引脚。")],
                openOcd=dict(targetScript=target_path, applicationFlashBytes=240 * 1024,
                             probes=[dict(id="wch-link", displayName="WCH-Link / WCH-LinkE", interfaceScript="interface/wch-link.cfg",
                                          transport="sdi", defaultSpeedKhz=4000)]),
            ))
        write("vendor/provenance.json", json.dumps(dict(
            source="MounRiver Studio 2 / WCH / CH595 NoneOS; PeripheralVersion 1.1",
            upstream="https://www.wch.cn/products/CH595.html",
            archives={f"CH595{suffix}.zip": digest for suffix, digest in ARCHIVES.items()},
            metadataSha256=METADATA,
            changes=["internal HSI PLL 80 MHz blank template", "240 KiB application Flash boundary",
                     "CH595 family ID and read-protection guard before download/debug"],
            license="WCH original source notices retained; use for WCH manufactured microcontrollers only.",
        ), indent=2, ensure_ascii=False))
        manifest = dict(formatVersion=1, id=PACK_ID, version=VERSION, displayName="CH595 · 标准库", vendor="WCH", devices=devices)
        write("manifest.json", json.dumps(manifest, indent=2, ensure_ascii=False))
        package = output / f"{PACK_ID}-{VERSION}.mcupack"
        subprocess.run(["dotnet", str(cli), "pack", str(stage), str(package)], check=True)
        (output / "index.json").write_text(json.dumps([dict(
            file=package.name, id=PACK_ID, version=VERSION, devices=[d["id"] for d in devices],
            sha256=hashlib.sha256(package.read_bytes()).hexdigest(),
        )], indent=2), encoding="utf-8")
    finally:
        for archive in archives.values():
            archive.close()


if __name__ == "__main__":
    main()
