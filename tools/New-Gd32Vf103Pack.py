"""Build GD32VF103 RISC-V StudioX pack from pinned official GigaDevice sources."""

import argparse
import hashlib
import json
import re
import subprocess
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / "examples/packs/gigadevice.gd32"
INFO = json.loads((RECIPE / "sources.json").read_text(encoding="utf-8"))["GD32VF103"]
DEVICES = json.loads((RECIPE / "vf103-devices.json").read_text(encoding="utf-8"))
PACK_ID = "gigadevice.gd32vf103"


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(root, name, data):
    path = root / name
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else data.encode("utf-8"))


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--sources", type=Path, required=True)
    p.add_argument("--output", type=Path, required=True, help="Existing or new GD32 pack output directory")
    a = p.parse_args()
    archive = a.sources / INFO["archive"]
    if digest(archive) != INFO["sha256"]:
        raise ValueError("GD32VF103 official SDK archive changed")
    folder = a.output / PACK_ID
    output_archive = folder / (PACK_ID + "-0.1.0.mcupack")
    if output_archive.exists():
        raise ValueError("GD32VF103 archive already exists")
    stage = folder / "source"
    stage.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(archive) as z:
        prefix = z.namelist()[0].split("/")[0] + "/"
        if INFO["commit"] not in prefix:
            raise ValueError("GD32VF103 SDK commit mismatch")
        entries = {n[len(prefix):]: n for n in z.namelist() if n.startswith(prefix) and not n.endswith("/")}

        def read(name):
            return z.read(entries[name])

        def copy_group(source_prefix, dest, extensions):
            names = []
            for n in sorted(entries):
                if n.startswith(source_prefix) and Path(n).suffix in extensions and n.count("/") == source_prefix.count("/"):
                    out = dest + Path(n).name
                    write(stage, out, read(n))
                    names.append(out)
            return names

        drivers = copy_group("Firmware/RISCV/drivers/", "sdk/riscv/", (".h", ".c"))
        peripheral_headers = copy_group("Firmware/GD32VF103_standard_peripheral/Include/", "sdk/peripheral/include/", (".h",))
        peripherals = copy_group("Firmware/GD32VF103_standard_peripheral/Source/", "sdk/peripheral/src/", (".c",))
        if len(drivers) < 5 or len(peripheral_headers) < 15 or len(peripherals) < 15:
            raise ValueError("Incomplete VF103 SDK")
        for n in ("start.S", "entry.S", "init.c", "handlers.c"):
            write(stage, "sdk/riscv/" + n, read("Firmware/RISCV/env_Eclipse/" + n))
        for n in ("gd32vf103.h", "system_gd32vf103.c", "system_gd32vf103.h"):
            write(stage, "sdk/device/" + n, read("Firmware/GD32VF103_standard_peripheral/" + n))
        write(stage, "sdk/device/gd32vf103_libopt.h", read("Template/gd32vf103_libopt.h"))
        for n in ("LICENSE", "Firmware/RISCV/LICENSE.TXT", "Firmware/GD32VF103_standard_peripheral/LICENSE.TXT"):
            write(stage, "licenses/" + Path(n).name.replace("LICENSE.TXT", Path(n).parent.name + "-LICENSE.TXT"), read(n))
        write(stage, "vendor/devices.json", (RECIPE / "vf103-devices.json").read_bytes())
        write(stage, "templates/main.c", '''#include <stdint.h>
#include "gd32vf103.h"

volatile uint32_t app_counter;
int main(void)
{
    /* 使用官方内部 IRC8M -> 48 MHz 初始化；无开发板引脚假设。 */
    for (;;) { ++app_counter; __asm__ volatile ("nop"); }
}
''')
        # Exact vendor linker memory and startup files are retained for each density.
        linkers = {}
        for code in "468B":
            name = f"GD32VF103x{code}.lds"
            data = read("Firmware/RISCV/env_Eclipse/" + name)
            match = re.search(rb"ORIGIN\s*=\s*0x08000000,\s*LENGTH\s*=\s*(\d+)k.*?ORIGIN\s*=\s*0x20000000,\s*LENGTH\s*=\s*(\d+)k", data, re.S | re.I)
            if not match:
                raise ValueError("Vendor linker memory changed: " + name)
            linkers[code] = (int(match[1]), int(match[2]))
            write(stage, "linker/" + name, data)
        manifest_devices = []
        for d in DEVICES:
            code = d["id"][10]
            if linkers[code] != (d["flashKib"], d["ramKib"]):
                raise ValueError(f"Datasheet/SDK memory mismatch: {d['id']}")
            source_names = ["sdk/riscv/start.S", "sdk/riscv/entry.S", "sdk/riscv/init.c", "sdk/riscv/handlers.c",
                            "sdk/riscv/n200_func.c", "sdk/device/system_gd32vf103.c",
                            "sdk/peripheral/src/gd32vf103_rcu.c", "sdk/peripheral/src/gd32vf103_gpio.c"]
            manifest_devices.append(dict(id=d["id"],
                displayName=f"{d['id']} · {d['package']} · {d['flashKib']}K / {d['ramKib']}K",
                architecture="riscv", flashOrigin=0x08000000, flashBytes=d["flashKib"]*1024,
                ramOrigin=0x20000000, ramBytes=d["ramKib"]*1024,
                toolsetId="riscv.xpack", toolsetVersion="1.0.0", compilerId="xpack-riscv-gcc-15.2.0",
                cpuFlags=["-march=rv32imac_zicsr_zifencei", "-mabi=ilp32", "-mcmodel=medlow"],
                defines=["HXTAL_VALUE=8000000U"], includeDirectories=["sdk/riscv", "sdk/device", "sdk/peripheral/include"],
                sources=source_names, linkerScript="linker/GD32VF103x" + code + ".lds",
                compileOptions=["-Os", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common"],
                linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections"],
                templates=[dict(id="spl", displayName="标准外设库 · 内部时钟最小工程",
                    description=f"{d['package']}；{d['flashKib']} KiB Flash / {d['ramKib']} KiB SRAM；官方 IRC8M → 48 MHz，无外设引脚配置。",
                    entryFile="templates/main.c")]))
    write(stage, "manifest.json", json.dumps(dict(formatVersion=1, id=PACK_ID, version="0.1.0",
        displayName="GD32VF103 · RISC-V 标准外设库", vendor="GigaDevice", devices=manifest_devices), ensure_ascii=False, indent=2))
    write(stage, "catalog.json", json.dumps(DEVICES, ensure_ascii=False, indent=2))
    write(stage, "provenance.json", json.dumps(dict(sdkRepository=INFO["url"], sdkCommit=INFO["commit"],
        sdkArchiveSha256=INFO["sha256"], deviceTable="GD32VF103 Datasheet Rev2.3 tables 2-1 and 2-2",
        deviceTableUrl="https://www.gd32mcu.com/data/documents/datasheet/GD32VF103_Datasheet_Rev2.3.pdf",
        license="Original GigaDevice and Nuclei licenses retained"), ensure_ascii=False, indent=2))
    write(stage, "README.md", (RECIPE / "VF103-README.md").read_bytes())
    cli = ROOT / "src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll"
    subprocess.run(["dotnet", str(cli), "pack", str(stage), str(output_archive)], check=True)
    index = a.output / "index.json"
    rows = json.loads(index.read_text(encoding="utf-8")) if index.exists() else []
    rows.append(dict(file=output_archive.relative_to(a.output).as_posix(), id=PACK_ID, version="0.1.0",
                     series="GD32VF103", devices=[d["id"] for d in DEVICES],
                     sha256=digest(output_archive), bytes=output_archive.stat().st_size))
    write(a.output, "index.json", json.dumps(rows, ensure_ascii=False, indent=2))
    print(f"GD32VF103: {len(DEVICES)} devices, {digest(output_archive)}")


if __name__ == "__main__":
    main()
