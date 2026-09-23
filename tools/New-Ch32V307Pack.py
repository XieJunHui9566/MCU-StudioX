"""Package the pinned local MounRiver WCH SDK. No tool binaries or downloads in mcupack."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile

ARCHIVES = {
    "VCT": "6aec0af44158481637f1e45cda716cae30cbcccb3b4b11988d63e68870bd3f91",
    "RCT": "380ce808a106d4dca90263578bb90bf6f87a680fe8ea17716610829bb0359f4e",
    "WCU": "0ef377c8595bb5c4fcfca907aa10c61de84c2ff1506381178adfdfdabb8e08fc",
}


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--sdk", type=Path, required=True, help="CH32V307/NoneOS directory")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    cli = repo / "src/StudioX.Cli/bin/Release/net10.0/StudioX.Cli.dll"
    if not cli.is_file():
        raise RuntimeError("Build the Release CLI first")
    metadata = {}
    for part, digest in ARCHIVES.items():
        archive = args.sdk / f"CH32V307{part}.zip"
        if hashlib.sha256(archive.read_bytes()).hexdigest() != digest:
            raise RuntimeError(f"SDK changed; review before updating recipe: {archive.name}")
        with zipfile.ZipFile(archive) as z:
            meta = z.read(".template").decode(errors="replace")
            metadata[part] = re.search(r"(?m)^MCU=(\w+)", meta)[1]
    args.output.mkdir(parents=True, exist_ok=False)
    stage = args.output / "source"
    stage.mkdir()

    def write(name, content):
        target = stage / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))

    with zipfile.ZipFile(args.sdk / "CH32V307VCT.zip") as z:
        for name in z.namelist():
            if name.endswith("/"):
                continue
            if name.startswith(("Core/", "Peripheral/", "Debug/")):
                write("sdk/" + name, z.read(name))
        write("sdk/Startup/startup_ch32v30x_D8C.S", z.read("Startup/startup_ch32v30x_D8C.S"))
        for name in ["ch32v30x_conf.h", "ch32v30x_it.h", "ch32v30x_it.c", "system_ch32v30x.h"]:
            write("system/" + name, z.read("User/" + name))
        clock = z.read("User/system_ch32v30x.c").decode().replace("\r\n", "\n").replace("\r", "\n")
        clock, count = re.subn(r"(?m)^#define SYSCLK_FREQ_96MHz_HSE\s+96000000\s*$", "#define SYSCLK_FREQ_144MHz_HSE 144000000", clock)
        if count != 1:
            raise RuntimeError("Clock selection changed")
        write("system/system_ch32v30x.c", "/* StudioX: default HSE 8 MHz / SYSCLK 144 MHz. */\n" + clock)
        linker = z.read("Ld/Link.ld").decode().replace("\r\n", "\n").replace("\r", "\n")
        linker, count = re.subn(r"MEMORY\s*\{.*?\}", "MEMORY\n{\n    FLASH (rx) : ORIGIN = 0x00000000, LENGTH = 256K\n    RAM (xrw)  : ORIGIN = 0x20000000, LENGTH = 64K\n}", linker, count=1, flags=re.S)
        if count != 1:
            raise RuntimeError("Linker memory definition changed")
        write("linker/ch32v307.ld", "/* StudioX: 256K Flash / 64K RAM option-byte split; flash executes at alias 0. */\n" + linker)
    for name in ["system_config.h", "system_config.c"]:
        write("system/" + name, (repo / "examples/packs/wch.ch32v307" / name).read_bytes())
    write("templates/main.c", (repo / "examples/packs/wch.ch32v307/main.c").read_bytes())
    write("interface/wch-link.cfg", (repo / "examples/packs/wch.ch32v307/wch-link.cfg").read_bytes())
    svd = args.sdk / "CH32V307xx.svd"
    write("svd/CH32V307xx.svd", svd.read_bytes())
    write("README.md", (repo / "examples/packs/wch.ch32v307/README.md").read_bytes())
    write("vendor/provenance.json", json.dumps({
        "source": "MounRiver Studio 2 / WCH / CH32V307 NoneOS; PeripheralVersion 3.1",
        "upstream": "https://github.com/openwch/ch32v307",
        "archives": {f"CH32V307{k}.zip": v for k, v in ARCHIVES.items()},
        "svdSha256": hashlib.sha256(svd.read_bytes()).hexdigest(),
        "changes": ["HSE 8 MHz -> 144 MHz selection", "256K Flash / 64K RAM linker split", "StudioX minimal main and system wrapper"],
        "license": "WCH source notices retained: software and binaries for WCH-manufactured microcontrollers only."
    }, indent=2))
    sources = sorted(p.relative_to(stage).as_posix() for p in (stage / "sdk").rglob("*.c"))
    sources += ["sdk/Startup/startup_ch32v30x_D8C.S", "system/system_ch32v30x.c", "system/ch32v30x_it.c", "system/system_config.c"]
    devices = []
    for part, device_id in metadata.items():
        chip_id = {"VCT": "0x30700508", "RCT": "0x30710508", "WCU": "0x30730508"}[part]
        target = "debug/" + device_id.lower() + ".cfg"
        write(target, (repo / "examples/packs/wch.ch32v307/ch32v307.cfg.in").read_text(encoding="utf-8").replace("@CHIP_ID@", chip_id).replace("@DEVICE_ID@", device_id))
        devices.append(dict(
            id=device_id, displayName=device_id, architecture="riscv",
            flashOrigin=0, flashBytes=256 * 1024, ramOrigin=0x20000000, ramBytes=64 * 1024,
            toolsetId="wch.riscv", toolsetVersion="1.0.0", compilerId="wch-gcc-12.2.0-v1.4",
            cpuFlags=["-march=rv32imac_xw", "-mabi=ilp32", "-msmall-data-limit=8", "-msave-restore"],
            defines=["CH32V30x_D8C", "HSE_VALUE=8000000"],
            includeDirectories=["sdk/Core", "sdk/Peripheral/inc", "sdk/Debug", "system"], sources=sources,
            linkerScript="linker/ch32v307.ld",
            compileOptions=["-Og", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common", "-fsigned-char"],
            linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections", "-Wl,--print-memory-usage"],
            templates=[dict(id="spl", displayName="标准库 · 精简 main", entryFile="templates/main.c",
                description="模板默认：外部 8 MHz 晶振 → 144 MHz；256 KiB Flash / 64 KiB SRAM。时钟与初始化位于 device/system；实际配置以工程代码为准。")],
            openOcd=dict(targetScript=target, applicationFlashBytes=256 * 1024, probes=[dict(id="wch-link", displayName="WCH-Link / WCH-LinkE", interfaceScript="interface/wch-link.cfg", transport="sdi", defaultSpeedKhz=6000)])))
    manifest = dict(formatVersion=1, id="wch.ch32v307", version="0.1.1", displayName="CH32V307 · 标准库", vendor="WCH", devices=devices)
    write("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    archive = args.output / "wch.ch32v307-0.1.1.mcupack"
    subprocess.run(["dotnet", str(cli), "pack", str(stage), str(archive)], check=True)
    index = [dict(file=archive.name, id=manifest["id"], version=manifest["version"], devices=list(metadata.values()), sha256=hashlib.sha256(archive.read_bytes()).hexdigest())]
    (args.output / "index.json").write_text(json.dumps(index, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
