"""Create a PY32F403 StudioX pack from a pinned OpenPuya mirror checkout.

No network or hardware calls are performed. The mirror is not an official Puya
GitHub organization; device capacities are checked against Puya product pages.
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / "examples/packs/puya.py32f403"
COMMIT = "982757404a416dd1e0fde921750d61167aed09ee"
DFP_NAME = "Puya.PY32F4xx_DFP.1.0.2.pack"
DFP_SHA256 = "f4d1e9b964aec78f460d1ddd79817b009c3222e7fe2f73c07f7e60ff26864380"
PACK_ID = "puya.py32f403"
VERSION = "0.1.0"

# 每项均有普冉官网完整料号页面；不由容量字母推测未列型号。
# 现行资料为 PY32F403 Datasheet V1.9 表 1-1（官网 2026-08-18）。
DEVICES = (
    ("PY32F403V1DT6", 384, 64, "LQFP100", "https://www.puyasemi.com/gongye/2848.html"),
    ("PY32F403R1DT6", 384, 64, "LQFP64", "https://www.puyasemi.com/gongye/2849.html"),
    ("PY32F403R2DT6", 384, 64, "LQFP64", "https://www.puyasemi.com/gongye/2851.html"),
    ("PY32F403R1CT6", 256, 64, "LQFP64", "https://www.puyasemi.com/gongye/2850.html"),
    ("PY32F403C1BT6", 128, 64, "LQFP48", "https://www.puyasemi.com/gongye/2855.html"),
    ("PY32F403C1CT6", 256, 64, "LQFP48", "https://www.puyasemi.com/gongye/2854.html"),
    ("PY32F403C1DT6", 384, 64, "LQFP48", "https://www.puyasemi.com/gongye/2852.html"),
    ("PY32F403C2DT6", 384, 64, "LQFP48", "https://www.puyasemi.com/gongye/2853.html"),
    ("PY32F403C1CU6", 256, 64, "QFN48", "https://www.puyasemi.com/gongye/2856.html"),
    ("PY32F403K1BU6", 128, 32, "QFN32", "https://www.puyasemi.com/gongye/2858.html"),
    ("PY32F403K1CU6", 256, 64, "QFN32", "https://www.puyasemi.com/gongye/3280.html"),
)

HAL_BASE = (
    "py32f403_hal.c", "py32f403_hal_cortex.c", "py32f403_hal_dma.c",
    "py32f403_hal_gpio.c", "py32f403_hal_rcc.c", "py32f403_hal_rcc_ex.c",
    "py32f403_hal_pwr.c", "py32f403_hal_pwr_ex.c",
)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def put(stage, relative, data):
    path = stage / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else data.encode("utf-8"))


def copy(stage, relative, source):
    put(stage, relative, source.read_bytes())


def copy_files(stage, relative, source):
    if not source.is_dir():
        raise FileNotFoundError(source)
    for path in sorted(source.iterdir()):
        if path.is_file():
            copy(stage, relative + "/" + path.name, path)


def memory_linker(original, flash, ram):
    text = original.read_text(encoding="utf-8-sig")
    # 原厂 EIDE GNU linker 含完整段、C 运行时符号和栈；只更改两处芯片容量。
    # MEMORY 行独立，可由离线验收器逐行确认，不在式子中推断尺寸。
    replacement = ("MEMORY\n{\n"
                   f"  RAM (xrw) : ORIGIN = 0x20000000, LENGTH = {ram * 1024}\n"
                   f"  FLASH (rx) : ORIGIN = 0x08000000, LENGTH = {flash * 1024}\n"
                   "}")
    text, count = re.subn(r"\bMEMORY\s*\{.*?\}", replacement, text, count=1, flags=re.S)
    if count != 1 or "ENTRY(Reset_Handler)" not in text or ".isr_vector" not in text:
        raise ValueError("Vendor linker layout changed")
    return text


def verify_source(source):
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"], text=True).strip()
    if revision != COMMIT or dirty:
        raise ValueError(f"Expected clean OpenPuya mirror commit {COMMIT}; got {revision}")
    dfp = source / "Pack/KEIL" / DFP_NAME
    if sha256(dfp) != DFP_SHA256:
        raise ValueError("Pinned mirror DFP hash mismatch")
    with zipfile.ZipFile(dfp) as archive:
        pdsc = archive.read("Puya.PY32F4xx_DFP.pdsc")
        svd = archive.read("CMSIS/SVD/PY32F403xx.svd")
    return dfp, pdsc, svd


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True, help="Clean pinned OpenPuya/PY32F4xx_Firmware checkout")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    parser.add_argument("--refresh-generated", action="store_true", help="Refresh this script's previously generated output")
    args = parser.parse_args()
    source = args.source.resolve(strict=True)
    output = args.output.resolve()
    if output.exists():
        if not args.refresh_generated:
            raise FileExistsError(f"Refusing to overwrite output: {output}")
        old_manifest = json.loads((output / "source-py32f403/manifest.json").read_text(encoding="utf-8"))
        old_source = json.loads((output / "source-py32f403/provenance.json").read_text(encoding="utf-8"))
        if old_manifest.get("id") != PACK_ID or old_source.get("mirrorCommit") != COMMIT:
            raise ValueError("Output was not generated from this locked PY32F403 source")
    dfp, pdsc, svd = verify_source(source)
    output.mkdir(parents=True, exist_ok=args.refresh_generated)
    stage = output / "source-py32f403"
    stage.mkdir(exist_ok=args.refresh_generated)
    cmsis = source / "Drivers/CMSIS"
    device = cmsis / "Device/PUYA/PY32F403/Include"
    hal = source / "Drivers/PY32F403_HAL_Driver"
    template = source / "Templates/PY32F403xx_Templates"
    ll_template = source / "Templates/PY32F403xx_Templates_LL"
    copy_files(stage, "sdk/cmsis", cmsis / "Include")
    copy_files(stage, "sdk/device", device)
    copy_files(stage, "sdk/driver/inc", hal / "Inc")
    copy_files(stage, "sdk/driver/src", hal / "Src")
    copy(stage, "sdk/system/system_py32f403.c", ll_template / "Src/system_py32f403.c")
    copy(stage, "sdk/startup/startup_py32f403xx.S", template / "EIDE/startup_py32f403xx.s")
    copy(stage, "sdk/config/py32f403_hal_conf.h", template / "Inc/py32f403_hal_conf.h")
    copy(stage, "support/runtime.c", ROOT / "examples/packs/st.stm32f407zg/support/runtime.c")
    for mode in ("cmsis", "hal", "ll"):
        copy(stage, f"templates/main-{mode}.c", RECIPE / f"main-{mode}.c")
    copy(stage, "licenses/OpenPuya-BSD-3-Clause.txt", source / "LICENSE")
    copy(stage, "licenses/CMSIS-LICENSE.txt", cmsis / "LICENSE.txt")
    put(stage, "vendor/Puya.PY32F4xx_DFP.pdsc", pdsc)
    put(stage, "vendor/PY32F403xx.svd", svd)
    copy(stage, "README.md", RECIPE / "README.md")
    linker = template / "EIDE/py32f403xd.ld"
    manifest_devices = []
    catalog = []
    for name, flash, ram, package, url in DEVICES:
        density = name[-3]
        if density not in "BCD":
            raise ValueError("Unknown F403 density: " + name)
        header = device / f"py32f403x{density}.h"
        if not header.is_file():
            raise FileNotFoundError(header)
        linker_path = f"linker/{name.lower()}.ld"
        put(stage, linker_path, memory_linker(linker, flash, ram))
        templates = [dict(id="cmsis", displayName="CMSIS · 寄存器", entryFile="templates/main-cmsis.c",
                          description=f"{flash} KiB Flash / {ram} KiB SRAM；HSI 内部时钟；无板级引脚假设。"),
                     dict(id="hal", displayName="Puya HAL", entryFile="templates/main-hal.c",
                          description=f"{flash} KiB Flash / {ram} KiB SRAM；原厂 HAL 基础模块。",
                          build=dict(defines=["USE_HAL_DRIVER"], includeDirectories=["sdk/driver/inc", "sdk/config"],
                                     sources=["sdk/driver/src/" + file for file in HAL_BASE],
                                     compileOptions=["-Wno-unused-parameter"], linkOptions=[])),
                     dict(id="ll", displayName="Puya LL", entryFile="templates/main-ll.c",
                          description=f"{flash} KiB Flash / {ram} KiB SRAM；原厂 LL 外设库。",
                          build=dict(defines=["USE_FULL_LL_DRIVER"], includeDirectories=["sdk/driver/inc", "sdk/config"],
                                     sources=["sdk/driver/src/" + file.name for file in sorted((hal / "Src").glob("*_ll_*.c"))],
                                     compileOptions=["-Wno-unused-parameter"], linkOptions=[]))]
        manifest_devices.append(dict(id=name,
            displayName=f"{name} · {package} · {flash}K Flash / {ram}K SRAM",
            architecture="arm", flashOrigin=0x08000000, flashBytes=flash * 1024,
            ramOrigin=0x20000000, ramBytes=ram * 1024,
            toolsetId="arm.gnu", toolsetVersion="1.0.0", compilerId="arm-gnu-15.2.rel1",
            cpuFlags=["-mcpu=cortex-m4", "-mthumb", "-mfpu=fpv4-sp-d16", "-mfloat-abi=hard"],
            defines=[f"PY32F403x{density}"], includeDirectories=["sdk/cmsis", "sdk/device"],
            sources=["sdk/startup/startup_py32f403xx.S", "sdk/system/system_py32f403.c", "support/runtime.c"],
            linkerScript=linker_path,
            compileOptions=["-Os", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common"],
            linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections", "-Wl,--no-warn-rwx-segments"],
            templates=templates))
        catalog.append(dict(id=name, flashKiB=flash, ramKiB=ram, package=package, officialProductPage=url,
                            header=header.name, status="software-only; no download/debug"))
    put(stage, "manifest.json", json.dumps(dict(formatVersion=1, id=PACK_ID, version=VERSION, vendor="Puya",
        displayName="PY32F403 · CMSIS / HAL / LL", devices=manifest_devices), ensure_ascii=False, indent=2))
    put(stage, "catalog.json", json.dumps(catalog, ensure_ascii=False, indent=2))
    put(stage, "provenance.json", json.dumps(dict(
        mirror="https://github.com/OpenPuya/PY32F4xx_Firmware",
        mirrorStatus="Unofficial community mirror; OpenPuya explicitly says it is not Puya's official repository",
        mirrorCommit=COMMIT, mirrorPack=DFP_NAME, mirrorPackSha256=DFP_SHA256,
        officialProductFamily="https://www.puyasemi.com/py32f403xilie589.html?tag=15",
        officialDatasheet="https://www.puyasemi.com/download_path/数据手册/MCU 微处理器/PY32F403_Datasheet_V1.9.pdf",
        officialListedFirmware="PY32F403 Firmware Library 1.4.8; not used here",
        officialListedDfp="PY32F4xx Keil DFP 1.0.12; not used here",
        mirrorDfpConflict="Old 1.0.2 DFP marks every F403xB as 64 KiB SRAM; current Puya product page and Datasheet V1.9 specify PY32F403K1BU6 as 32 KiB. Exact-SKU official values take precedence.",
        exclusions="New -C revision has a separate datasheet/reference manual and is not mapped to this older fixed firmware mirror.",
        startupSource="Templates/PY32F403xx_Templates/EIDE/startup_py32f403xx.s",
        systemSource="Templates/PY32F403xx_Templates_LL/Src/system_py32f403.c",
        linkerSource="Templates/PY32F403xx_Templates/EIDE/py32f403xd.ld; MEMORY replaced per official SKU",
        licenses=["licenses/OpenPuya-BSD-3-Clause.txt", "licenses/CMSIS-LICENSE.txt"],
        sourceFileSha256={p.relative_to(stage).as_posix(): sha256(p) for p in sorted((stage / "sdk").rglob("*")) if p.is_file()},
    ), ensure_ascii=False, indent=2))
    archive = output / f"{PACK_ID}-{VERSION}.mcupack"
    next_archive = archive.with_name(archive.name + ".next") if args.refresh_generated else archive
    if next_archive.exists():
        raise FileExistsError(next_archive)
    cli = ROOT / "src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll"
    subprocess.run(["dotnet", str(cli), "pack", str(stage), str(next_archive)], check=True)
    if args.refresh_generated:
        os.replace(next_archive, archive)
    result = dict(file=archive.name, id=PACK_ID, version=VERSION,
                  devices=[item["id"] for item in manifest_devices], sha256=sha256(archive))
    put(output, "index.json", json.dumps([result], ensure_ascii=False, indent=2))
    print(f"{PACK_ID}: {len(manifest_devices)} SKUs, {result['sha256']}")


if __name__ == "__main__":
    main()
