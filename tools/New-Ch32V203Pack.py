"""Build the CH32V203 pack from pinned MounRiver SDK archives; no downloads."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import zipfile


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--sdk", type=Path, required=True, help="CH32V203/NoneOS directory")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    recipe = repo / "examples/packs/wch.ch32v203"
    definitions = json.loads((recipe / "devices.json").read_text(encoding="utf-8"))
    archives = {}
    for device in definitions:
        path = args.sdk / f"CH32V203{device['archive']}.zip"
        if hashlib.sha256(path.read_bytes()).hexdigest() != device["sha256"]:
            raise RuntimeError(f"SDK changed; review before updating recipe: {path.name}")
        with zipfile.ZipFile(path) as z:
            if re.search(r"(?m)^MCU=(\w+)", z.read(".template").decode(errors="replace"))[1] != device["id"]:
                raise RuntimeError(f"SDK part mismatch: {path.name}")
        archives[path.name] = device["sha256"]
    args.output.mkdir(parents=True, exist_ok=False)
    stage = args.output / "source"

    def write(name, content):
        target = stage / name
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))

    # D6 与 D8 共用官方库，独立选择启动文件及宏；CCT 使用不同的 V205 外设库。
    for family, archive, clock, old_clock in [("v20x", "C8T", 144, 96), ("v205", "CCT", 160, 160)]:
        chip = "ch32" + family
        with zipfile.ZipFile(args.sdk / f"CH32V203{archive}.zip") as z:
            for name in z.namelist():
                if not name.endswith("/") and name.startswith(("Core/", "Peripheral/", "Debug/")):
                    write(f"sdk/{family}/{name}", z.read(name))
            startups = ["startup_ch32v20x_D6.S", "startup_ch32v20x_D8.S"] if family == "v20x" else ["startup_ch32v205.S"]
            for name in startups:
                write("sdk/Startup/" + name, z.read("Startup/" + name))
            for suffix in ["_conf.h", "_it.h", "_it.c"]:
                write(f"system/{family}/{chip}{suffix}", z.read(f"User/{chip}{suffix}"))
            write(f"system/{family}/system_{chip}.h", z.read(f"User/system_{chip}.h"))
            source = z.read(f"User/system_{chip}.c").decode().replace("\r\n", "\n").replace("\r", "\n")
            source, count = re.subn(rf"(?m)^#define SYSCLK_FREQ_{old_clock}MHz_HSE\s+{old_clock}000000\s*$", f"#define SYSCLK_FREQ_{clock}MHz_HSI {clock}000000", source)
            if count != 1:
                raise RuntimeError("Clock selector changed: " + archive)
            write(f"system/{family}/system_{chip}.c", "/* StudioX: internal HSI 8 MHz / PLL; no external crystal required. */\n" + source)
            write(f"system/{family}/system_config.h", f'#pragma once\n#include "{chip}.h"\n#include "debug.h"\n#ifdef __cplusplus\nextern "C" {{\n#endif\nvoid System_Init(void);\n#ifdef __cplusplus\n}}\n#endif\n')
            priority = "    NVIC_PriorityGroupConfig(NVIC_PriorityGroup_1);\n" if family == "v20x" else ""
            write(f"system/{family}/system_config.c", '#include "system_config.h"\n\nvoid System_Init(void)\n{\n    /* 官方启动代码已调用 SystemInit；此处不初始化板级外设。 */\n' + priority + '    SystemCoreClockUpdate();\n    Delay_Init();\n}\n')
    write("templates/main.c", (recipe / "main.c").read_bytes())
    write("interface/wch-link.cfg", (repo / "examples/packs/wch.ch32v307/wch-link.cfg").read_bytes())
    write("README.md", (recipe / "README.md").read_bytes())
    write("vendor/devices.json", (recipe / "devices.json").read_bytes())
    devices = []
    for device in definitions:
        device_id, family = device["id"], device["family"]
        chip = "ch32" + family
        with zipfile.ZipFile(args.sdk / f"CH32V203{device['archive']}.zip") as z:
            linker = z.read("Ld/Link.ld").decode().replace("\r\n", "\n").replace("\r", "\n")
        linker, count = re.subn(r"MEMORY\s*\{.*?\}", f"MEMORY\n{{\n    FLASH (rx) : ORIGIN = 0, LENGTH = {device['flashKib']}K\n    RAM (xrw) : ORIGIN = 0x20000000, LENGTH = {device['ramKib']}K\n}}", linker, count=1, flags=re.S)
        if count != 1:
            raise RuntimeError("Linker memory changed: " + device_id)
        link_path = "linker/" + device_id.lower() + ".ld"
        write(link_path, linker)
        guard = "# Fixed memory layout; no V307 split check."
        if device["define"] == "CH32V20x_D8":
            guard = '''set user [lindex [read_memory 0x1ffff802 16 1] 0]
    if {((($user >> 8) ^ $user) & 0xff) != 0xff} { error "Invalid option-byte complement" }
    if {(($user >> 6) & 3) < 2} { error "Memory split mismatch: project requires 160 KiB Flash / 32 KiB RAM; option bytes unchanged" }'''
        target_path = "debug/" + device_id.lower() + ".cfg"
        target = (recipe / "ch32v203.cfg.in").read_text(encoding="utf-8")
        for key, value in {"DEVICE_ID": device_id, "CHIP_MISMATCH": " && ".join("$id != " + id for id in device["chipIds"]), "MEMORY_GUARD": guard, "FLASH_KIB": str(device["flashKib"]), "RAM_KIB": str(device["ramKib"])}.items():
            target = target.replace("@" + key + "@", value)
        write(target_path, target)
        startup = "startup_ch32v205.S" if family == "v205" else "startup_ch32v20x_" + device["define"].split("_")[-1] + ".S"
        sources = sorted(p.relative_to(stage).as_posix() for p in (stage / "sdk" / family).rglob("*.c"))
        sources += ["sdk/Startup/" + startup, f"system/{family}/system_{chip}.c", f"system/{family}/{chip}_it.c", f"system/{family}/system_config.c"]
        clock = 160 if family == "v205" else 144
        devices.append(dict(id=device_id, displayName=device_id, architecture="riscv", flashOrigin=0, flashBytes=device["flashKib"] * 1024,
            ramOrigin=0x20000000, ramBytes=device["ramKib"] * 1024, toolsetId="wch.riscv", toolsetVersion="1.0.0", compilerId="wch-gcc-12.2.0-v1.4",
            # GCC 12 的汇编器没有 b 的默认版本；使用厂商对应 multilib 的显式扩展。
            cpuFlags=["-march=" + ("rv32imc_zba_zbb_zbc_zbs_xw" if family == "v205" else "rv32imac_xw"), "-mabi=ilp32", "-msmall-data-limit=8", "-msave-restore"],
            defines=[device["define"]], includeDirectories=[f"sdk/{family}/Core", f"sdk/{family}/Peripheral/inc", f"sdk/{family}/Debug", f"system/{family}"], sources=sources,
            linkerScript=link_path, compileOptions=["-Og", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common", "-fsigned-char"],
            linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections", "-Wl,--print-memory-usage"],
            templates=[dict(id="spl", displayName="标准库 · 精简 main", entryFile="templates/main.c", description=f"模板默认：内部 HSI 8 MHz → {clock} MHz；{device['flashKib']} KiB Flash / {device['ramKib']} KiB SRAM。时钟与初始化位于 device/system；实际配置以工程代码为准。")],
            openOcd=dict(targetScript=target_path, applicationFlashBytes=device["flashKib"] * 1024, probes=[dict(id="wch-link", displayName="WCH-Link / WCH-LinkE", interfaceScript="interface/wch-link.cfg", transport="sdi", defaultSpeedKhz=4000)])))
    write("vendor/provenance.json", json.dumps(dict(source="MounRiver Studio 2 / WCH / CH32V203 NoneOS; V20x SPL 2.4 and V205 SPL 1.2",
        upstream="https://github.com/openwch/ch32v20x", archives=archives,
        changes=["HSI / PLL default clock instead of external crystal", "explicit per-device memory and startup", "CCT RVB expanded to zba_zbb_zbc_zbs for vendor GCC12 multilib", "minimal StudioX main/system wrapper", "part/protection/split guard and page erase"],
        license="WCH source notices retained: software and binaries for WCH-manufactured microcontrollers only."), indent=2))
    manifest = dict(formatVersion=1, id="wch.ch32v203", version="0.1.0", displayName="CH32V203 · 标准库", vendor="WCH", devices=devices)
    write("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    archive = args.output / "wch.ch32v203-0.1.0.mcupack"
    subprocess.run(["dotnet", str(repo / "src/StudioX.Cli/bin/Release/net10.0/StudioX.Cli.dll"), "pack", str(stage), str(archive)], check=True)
    (args.output / "index.json").write_text(json.dumps([dict(file=archive.name, id=manifest["id"], version=manifest["version"], devices=[d["id"] for d in devices], sha256=hashlib.sha256(archive.read_bytes()).hexdigest())], indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
