"""Build a pinned StudioX CH592 pack from local official MounRiver SDK files."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile


PACK_ID = "wch.ch592"
VERSION = "0.1.0"
ARCHIVES = {
    "D": "9386d24b3681779c930115021d07baff2213324e5dea33951198a73637f7d836",
    "F": "2747e12c8ff52c3e6031f2cefa1f7e58f1a57d6e46e08ff5a76c6806613e5bb3",
    "X": "1b13a45a1e522751b3e9e11a0284f55a610d8851cb3a19d4825e160196eddc9f",
}
METADATA = {
    "CH59Xxx.svd": "d27a882906abdb089991cc87222adee72635c71c4b424d6c76de8cda6df11b8c",
    "CH59X-targetProcessor.json": "2d782e80f96e33378098f58cbfaa49fc6edf7f9c5bc4984d1cf37f47afe78e4d",
    "CH59X-flash.json": "55170a67ee35e16db2b84b3deff46b9506b4d1c63b4eedd29d732ad9aa6b15f3",
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
    parser.add_argument("--sdk", type=Path, required=True, help="MounRiver CH59X/NoneOS directory")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    recipe = repo / "examples/packs/wch.ch592"
    cli = repo / "src/StudioX.Cli/bin/Release/net10.0/StudioX.Cli.dll"
    if not cli.is_file():
        raise RuntimeError("Build StudioX.Cli in Release before packaging")
    sdk = args.sdk.resolve(strict=True)
    metadata = {name: checked_bytes(sdk / name, sha) for name, sha in METADATA.items()}
    processor = json.loads(metadata["CH59X-targetProcessor.json"])
    flash = json.loads(metadata["CH59X-flash.json"])
    if processor["architecture"] != "rv32i" or processor["integer_ABI"] != "ilp32" or flash["type"] != "CH59x" or flash["id"] != 11:
        raise RuntimeError("CH59X SDK target metadata changed")

    archives: dict[str, zipfile.ZipFile] = {}
    try:
        for suffix, digest in ARCHIVES.items():
            archive_path = sdk / f"CH592{suffix}.zip"
            checked_bytes(archive_path, digest)
            archive = zipfile.ZipFile(archive_path)
            description = archive.read(".template").decode("utf-8", errors="replace")
            if not re.search(rf"(?m)^MCU=CH592{suffix}$", description):
                archive.close()
                raise RuntimeError(f"CH592{suffix}.zip identifies a different device")
            linker = archive.read("Ld/Link.ld").decode("ascii", errors="replace")
            if not re.search(r"FLASH\s*\(rx\)\s*:\s*ORIGIN\s*=\s*0x00000000\s*,\s*LENGTH\s*=\s*448K", linker) or not re.search(
                r"RAM\s*\(xrw\)\s*:\s*ORIGIN\s*=\s*0x20000000\s*,\s*LENGTH\s*=\s*26K", linker
            ):
                archive.close()
                raise RuntimeError(f"CH592{suffix}.zip linker memory range changed")
            archives[suffix] = archive

        # 官方三份模板仅 Eclipse 元数据不同；若 SDK 资源开始分化，则停止合包。
        sdk_files = [name for name in archives["D"].namelist() if not name.endswith("/") and name.startswith(SDK_PREFIXES)]
        for suffix in "FX":
            names = set(archives[suffix].namelist())
            if any(name not in names or archives[suffix].read(name) != archives["D"].read(name) for name in sdk_files):
                raise RuntimeError(f"CH592{suffix}.zip support differs from CH592D.zip; review per-device assets")
        if not sdk_files or "Startup/startup_CH592.S" not in sdk_files or "Ld/Link.ld" not in sdk_files or "StdPeriphDriver/libISP592.a" not in sdk_files:
            raise RuntimeError("CH592 SDK support files are incomplete")
        if b"FLASH_ROM_MAX_SIZE  0x070000" not in archives["D"].read("StdPeriphDriver/inc/ISP592.h"):
            raise RuntimeError("CH592 IAP library application Flash bound changed")

        output = args.output.resolve()
        output.mkdir(parents=True, exist_ok=False)
        stage = output / "source"

        def write(name: str, content: bytes | str) -> None:
            target = stage / name
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))

        for name in sdk_files:
            write("sdk/" + name, archives["D"].read(name))
        write("svd/CH59Xxx.svd", metadata["CH59Xxx.svd"])
        write("vendor/CH59X-targetProcessor.json", metadata["CH59X-targetProcessor.json"])
        write("vendor/CH59X-flash.json", metadata["CH59X-flash.json"])
        write("templates/main.c", (recipe / "main.c").read_bytes())
        write("interface/wch-link.cfg", (recipe / "wch-link.cfg").read_bytes())
        write("README.md", (recipe / "README.md").read_bytes())

        sources = sorted("sdk/" + name for name in sdk_files if name.startswith("StdPeriphDriver/") and name.endswith(".c"))
        sources += ["sdk/RVMSIS/core_riscv.c", "sdk/Startup/startup_CH592.S", "sdk/StdPeriphDriver/libISP592.a"]
        devices = []
        for suffix in "DFX":
            device_id = "CH592" + suffix
            target_path = "debug/" + device_id.lower() + ".cfg"
            target = (recipe / "ch592.cfg.in").read_text(encoding="utf-8").replace("@DEVICE_ID@", device_id)
            write(target_path, target)
            devices.append(dict(
                id=device_id,
                displayName=device_id,
                architecture="riscv",
                flashOrigin=0,
                flashBytes=448 * 1024,
                ramOrigin=0x20000000,
                ramBytes=26 * 1024,
                toolsetId="wch.riscv",
                toolsetVersion="1.0.0",
                compilerId="wch-gcc-12.2.0-v1.4",
                cpuFlags=["-march=rv32imac", "-mabi=ilp32", "-msmall-data-limit=8"],
                defines=["FREQ_SYS=60000000"],
                includeDirectories=["sdk/RVMSIS", "sdk/StdPeriphDriver/inc"],
                sources=sources,
                linkerScript="sdk/Ld/Link.ld",
                compileOptions=["-Og", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common", "-fsigned-char"],
                linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections", "-Wl,--print-memory-usage"],
                templates=[dict(id="spl", displayName="标准库 · 精简 main", entryFile="templates/main.c",
                                description="WCH 官方 60 MHz PLL 时钟；448 KiB 应用 Flash / 26 KiB SRAM。模板不使用板级引脚。")],
                openOcd=dict(targetScript=target_path, applicationFlashBytes=448 * 1024,
                             probes=[dict(id="wch-link", displayName="WCH-Link / WCH-LinkE", interfaceScript="interface/wch-link.cfg",
                                          transport="sdi", defaultSpeedKhz=4000)]),
            ))
        write("vendor/provenance.json", json.dumps(dict(
            source="MounRiver Studio 2 / WCH / CH59X NoneOS; PeripheralVersion 1.8",
            upstream="https://www.wch.cn/products/CH592.html",
            archives={f"CH592{suffix}.zip": digest for suffix, digest in ARCHIVES.items()},
            metadataSha256=METADATA,
            changes=["blank pin-neutral main", "explicit 448 KiB application Flash and 26 KiB SRAM",
                     "CH592 family ID, read-protection and debug-enable guard before download/debug"],
            license="WCH original source notices and vendor libISP592.a retained; use for WCH manufactured microcontrollers only.",
        ), indent=2, ensure_ascii=False))
        manifest = dict(formatVersion=1, id=PACK_ID, version=VERSION, displayName="CH592 · 标准库", vendor="WCH", devices=devices)
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
