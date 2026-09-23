"""Build independent offline PY32F410/F420 packs from pinned OpenPuya mirrors.

OpenPuya is an unofficial mirror. Exact SKUs and memory values are taken from
current Puya datasheets/product pages, not inferred from old DFP wildcard IDs.
No probe, hardware, download or debugger access is performed.
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
RECIPE = ROOT / "examples/packs/puya.py32f410-f420"
VERSION = "0.1.0"
CONFIG = {
    "F410": {
        "commit": "b14b4511ef0229254f27665c256e46676520e915",
        "dfp": "Puya.PY32F4xx_DFP.1.0.2.pack",
        "dfpSha256": "f4d1e9b964aec78f460d1ddd79817b009c3222e7fe2f73c07f7e60ff26864380",
        "datasheet": "https://www.puyasemi.com/download_path/数据手册/MCU 微处理器/PY32F410_Datasheet_V1.3.pdf",
        "officialFirmware": "PY32F410 Firmware Library 0.5.9",
        "devices": [
            ("PY32F410R1BT7", 128, 16, "LQFP64", "PY32F410xB"),
            ("PY32F410C1BT7", 128, 16, "LQFP48", "PY32F410xB"),
            ("PY32F410C2BU7", 128, 16, "QFN48", "PY32F410xB"),
            ("PY32F410K1BT7", 128, 16, "LQFP32", "PY32F410xB"),
            ("PY32F410K1BU7", 128, 16, "QFN32", "PY32F410xB"),
            ("PY32F410G1BU7", 128, 16, "QFN28", "PY32F410xB"),
        ],
        "cpuFlags": ["-mcpu=cortex-m4", "-mthumb"],
        "modes": ["cmsis", "hal", "ll"],
    },
    "F420": {
        "commit": "15843708e31e832f0176867841a8ffaafb4d3e21",
        "dfp": "Puya.PY32F4xx_DFP.1.0.8.pack",
        "dfpSha256": "a0804584559e5f79e50d6af5e23db0fcff486dc6847c98ab0f730e745d5b0a4f",
        "datasheet": "https://www.puyasemi.com/download_path/数据手册/MCU 微处理器/PY32F42x系列数据手册_V0.3.pdf",
        "officialFirmware": "PY32F420 Firmware Library 0.5.3",
        "devices": [
            ("PY32F420R1CT7", 256, 48, "LQFP64", "PY32F420xC"),
            ("PY32F420C2CT7", 256, 48, "LQFP48", "PY32F420xC"),
        ],
        "cpuFlags": ["-mcpu=cortex-m4", "-mthumb", "-mfpu=fpv4-sp-d16", "-mfloat-abi=hard"],
        "modes": ["cmsis", "hal"],
    },
}
HAL_SUFFIXES = ("hal.c", "hal_cortex.c", "hal_dma.c", "hal_gpio.c",
                "hal_rcc.c", "hal_rcc_ex.c", "hal_pwr.c", "hal_pwr_ex.c")


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def put(stage, relative, content):
    target = stage / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(content if isinstance(content, bytes) else content.encode("utf-8"))


def copy(stage, relative, source):
    put(stage, relative, source.read_bytes())


def copy_files(stage, relative, folder):
    if not folder.is_dir():
        raise FileNotFoundError(folder)
    for source in sorted(folder.iterdir()):
        if source.is_file():
            copy(stage, relative + "/" + source.name, source)


def check_mirror(source, cfg):
    revision = subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"], text=True).strip()
    dirty = subprocess.check_output(["git", "-C", str(source), "status", "--porcelain", "--untracked-files=no"], text=True).strip()
    if revision != cfg["commit"] or dirty:
        raise ValueError(f"Mirror must be clean at {cfg['commit']}; got {revision}")
    dfp = source / "Pack/KEIL" / cfg["dfp"]
    if digest(dfp) != cfg["dfpSha256"]:
        raise ValueError(f"DFP hash changed: {dfp}")
    return dfp


def translated_startup(source):
    """Use the vendor ARMASM vector names/order with a GNU C reset routine."""
    block = re.search(r"(?ms)^__Vectors\s+DCD.*?^__Vectors_End", source)
    if block is None:
        raise ValueError("Vendor vector table missing")
    vectors = re.findall(r"\bDCD\s+([A-Za-z_][A-Za-z_0-9]*|0)\b", block.group())
    if vectors[:2] != ["__initial_sp", "Reset_Handler"] or len(vectors) < 60:
        raise ValueError("Vendor vector table changed")
    handlers = sorted(set(vectors) - {"__initial_sp", "Reset_Handler", "0"})
    weak = "\n".join(f'void {name}(void) __attribute__((weak, alias("Default_Handler")));' for name in handlers)
    entries = ",\n    ".join("(uintptr_t)&_estack" if name == "__initial_sp" else
                             "0" if name == "0" else f"(uintptr_t){name}" for name in vectors)
    return f'''/* Vector order/names derived from pinned Puya MDK startup; GNU C adaptation. */
#include <stdint.h>
extern uint32_t _estack, _sidata, _sdata, _edata, _sbss, _ebss;
extern void SystemInit(void), __libc_init_array(void);
extern int main(void);
void Reset_Handler(void);
void Default_Handler(void) {{ for (;;) {{ __asm volatile ("nop"); }} }}
{weak}
__attribute__((used, section(".isr_vector")))
const uintptr_t g_pfnVectors[] = {{
    {entries}
}};
void Reset_Handler(void)
{{
    uint32_t *src = &_sidata;
    for (uint32_t *dst = &_sdata; dst < &_edata;) *dst++ = *src++;
    for (uint32_t *dst = &_sbss; dst < &_ebss;) *dst++ = 0;
    SystemInit();
    __libc_init_array();
    (void)main();
    for (;;) {{ __asm volatile ("nop"); }}
}}
''', len(vectors)


def linker_for(base, flash, ram):
    text = base.read_text(encoding="utf-8-sig")
    memory = ("MEMORY\n{\n"
              f"  RAM (xrw) : ORIGIN = 0x20000000, LENGTH = {ram * 1024}\n"
              f"  FLASH (rx) : ORIGIN = 0x08000000, LENGTH = {flash * 1024}\n"
              "}")
    text, count = re.subn(r"\bMEMORY\s*\{.*?\}", memory, text, count=1, flags=re.S)
    if count != 1 or ".isr_vector" not in text or "ENTRY(Reset_Handler)" not in text:
        raise ValueError("Linker layout changed")
    return text


def build_family(family, source_root, output, cli, refresh):
    cfg = CONFIG[family]
    series = "PY32" + family
    source = source_root / (series + "_Firmware-mirror")
    dfp = check_mirror(source, cfg)
    stage = output / ("source-" + series.lower())
    if stage.exists() and not refresh:
        raise FileExistsError(stage)
    if stage.exists():
        previous = json.loads((stage / "provenance.json").read_text(encoding="utf-8"))
        if previous.get("mirrorCommit") != cfg["commit"]:
            raise ValueError("Refusing to replace an unrelated staging directory")
    stage.mkdir(parents=True, exist_ok=refresh)
    cmsis = source / "Drivers/CMSIS"
    device = cmsis / "Device/PUYA" / series / "Include"
    driver = source / "Drivers" / (series + "_HAL_Driver")
    template = source / "Templates" / (series + "xx_Templates")
    copy_files(stage, "sdk/cmsis", cmsis / "Include")
    copy_files(stage, "sdk/device", device)
    copy_files(stage, "sdk/driver/inc", driver / "Inc")
    copy_files(stage, "sdk/driver/src", driver / "Src")
    # F410 HAL example system.c references HAL-only GPIO_PIN_11 and
    # RCC_PLLSOURCE_HSE even in CMSIS mode. The vendor LL example supplies
    # equivalent register-level SystemInit/SystemCoreClockUpdate for all modes.
    system_source = (source / "Templates" / (series + "xx_Templates_LL") / "Src" /
                     ("system_" + series.lower() + ".c") if family == "F410" else
                     template / "Src" / ("system_" + series.lower() + ".c"))
    copy(stage, "sdk/system/system.c", system_source)
    startup_source = template / "MDK-ARM" / ("startup_" + series.lower() + "xx.s")
    startup, vectors = translated_startup(startup_source.read_text(encoding="utf-8", errors="replace"))
    put(stage, "sdk/startup/startup.c", startup)
    copy(stage, "sdk/config/" + series.lower() + "_hal_conf.h", template / "Inc" / (series.lower() + "_hal_conf.h"))
    copy(stage, "support/runtime.c", ROOT / "examples/packs/st.stm32f407zg/support/runtime.c")
    copy(stage, "licenses/OpenPuya-BSD-3-Clause.txt", source / "LICENSE")
    copy(stage, "licenses/CMSIS-LICENSE.txt", cmsis / "LICENSE.txt")
    for mode in cfg["modes"]:
        copy(stage, f"templates/main-{mode}.c", RECIPE / f"main-{mode}.c")
    copy(stage, "README.md", RECIPE / "README.md")
    with zipfile.ZipFile(dfp) as archive:
        pdsc = archive.read("Puya.PY32F4xx_DFP.pdsc")
        svd = archive.read("CMSIS/SVD/" + series + "xx.svd")
    put(stage, "vendor/Puya.PY32F4xx_DFP.pdsc", pdsc)
    put(stage, f"vendor/{series}xx.svd", svd)
    linker_source = ROOT / "artifacts/packs/Puya-F403-0.1.0/source-py32f403/linker/py32f403r1ct6.ld"
    if not linker_source.is_file():
        raise FileNotFoundError(linker_source)
    hal_sources = [f"sdk/driver/src/{series.lower()}_{suffix}" for suffix in HAL_SUFFIXES]
    if any(not (stage / name).is_file() for name in hal_sources):
        raise FileNotFoundError("Missing basic HAL source")
    ll_sources = ["sdk/driver/src/" + p.name for p in sorted((driver / "Src").glob("*_ll_*.c"))]
    if "ll" in cfg["modes"] and not ll_sources:
        raise FileNotFoundError("Missing LL source")
    manifest_devices = []
    catalog = []
    for name, flash, ram, package, macro in cfg["devices"]:
        header = device / (macro.lower() + ".h")
        if not header.is_file():
            raise FileNotFoundError(header)
        link = f"linker/{name.lower()}.ld"
        put(stage, link, linker_for(linker_source, flash, ram))
        modes = []
        for mode in cfg["modes"]:
            overlay = None
            if mode != "cmsis":
                overlay = dict(defines=["USE_HAL_DRIVER"] if mode == "hal" else ["USE_FULL_LL_DRIVER"],
                               includeDirectories=["sdk/driver/inc", "sdk/config"],
                               sources=hal_sources if mode == "hal" else ll_sources,
                               compileOptions=["-Wno-unused-parameter"], linkOptions=[])
            modes.append(dict(id=mode,
                              displayName={"cmsis": "CMSIS · 寄存器", "hal": "Puya HAL", "ll": "Puya LL"}[mode],
                              description=f"{flash} KiB Flash / {ram} KiB SRAM；内部时钟；无板级引脚假设。",
                              entryFile=f"templates/main-{mode}.c", build=overlay))
        manifest_devices.append(dict(id=name, displayName=f"{name} · {package} · {flash}K/{ram}K",
            architecture="arm", flashOrigin=0x08000000, flashBytes=flash * 1024,
            ramOrigin=0x20000000, ramBytes=ram * 1024,
            toolsetId="arm.gnu", toolsetVersion="1.0.0", compilerId="arm-gnu-15.2.rel1",
            cpuFlags=cfg["cpuFlags"], defines=[macro], includeDirectories=["sdk/cmsis", "sdk/device"],
            sources=["sdk/startup/startup.c", "sdk/system/system.c", "support/runtime.c"],
            linkerScript=link,
            compileOptions=["-Os", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common"],
            linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs",
                         "-Wl,--gc-sections", "-Wl,--no-warn-rwx-segments"], templates=modes))
        catalog.append(dict(id=name, flashKiB=flash, ramKiB=ram, package=package,
                            sdkDefine=macro, status="software-only; no download/debug"))
    pack_id = "puya." + series.lower()
    put(stage, "manifest.json", json.dumps(dict(formatVersion=1, id=pack_id, version=VERSION,
        vendor="Puya", displayName=series + " · CMSIS / HAL" + (" / LL" if "ll" in cfg["modes"] else ""),
        devices=manifest_devices), ensure_ascii=False, indent=2))
    put(stage, "catalog.json", json.dumps(catalog, ensure_ascii=False, indent=2))
    put(stage, "provenance.json", json.dumps(dict(
        mirror="https://github.com/OpenPuya/" + series + "_Firmware",
        mirrorStatus="Unofficial community mirror, not Puya's official repository",
        mirrorCommit=cfg["commit"], mirrorDfp=dfp.name, mirrorDfpSha256=cfg["dfpSha256"],
        officialDatasheet=cfg["datasheet"], officialListedFirmware=cfg["officialFirmware"],
        officialListedDfp="PY32F4xx Keil DFP 1.0.12; not used by this build",
        exclusions=("PY32F410G18U7: official product page says 8 KiB SRAM while Datasheet V1.3 says 16 KiB; excluded" if family == "F410" else
                    "Other older DFP wildcard variants have no exact current official SKU confirmation"),
        startupSource=startup_source.relative_to(source).as_posix(), startupVectorCount=vectors,
        systemSource=system_source.relative_to(source).as_posix(),
        linkerSource=linker_source.relative_to(ROOT).as_posix(), linkerSourceSha256=digest(linker_source),
        linkerModification="MEMORY replaced with official exact-SKU Flash/SRAM capacity",
        licenses=["licenses/OpenPuya-BSD-3-Clause.txt", "licenses/CMSIS-LICENSE.txt"],
        sourceFileSha256={p.relative_to(stage).as_posix(): digest(p) for p in sorted((stage / "sdk").rglob("*")) if p.is_file()},
    ), ensure_ascii=False, indent=2))
    archive = output / f"{pack_id}-{VERSION}.mcupack"
    next_archive = archive.with_name(archive.name + ".next") if refresh else archive
    if next_archive.exists():
        raise FileExistsError(next_archive)
    subprocess.run(["dotnet", str(cli), "pack", str(stage), str(next_archive)], check=True)
    if refresh:
        os.replace(next_archive, archive)
    return dict(file=archive.name, id=pack_id, version=VERSION,
                devices=[d["id"] for d in manifest_devices], sha256=digest(archive))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--family", choices=CONFIG)
    parser.add_argument("--refresh-generated", action="store_true")
    args = parser.parse_args()
    cli = ROOT / "src/StudioX.Cli/bin/Release/net10.0/StudioX.Cli.dll"
    if not cli.is_file():
        raise FileNotFoundError(cli)
    args.output.mkdir(parents=True, exist_ok=True)
    families = [args.family] if args.family else CONFIG
    rows = [build_family(f, args.source_root, args.output, cli, args.refresh_generated) for f in families]
    if args.family:
        existing = args.output / "index.json"
        if existing.exists():
            rows = [r for r in json.loads(existing.read_text(encoding="utf-8")) if r["id"] != rows[0]["id"]] + rows
    put(args.output, "index.json", json.dumps(sorted(rows, key=lambda r: r["id"]), ensure_ascii=False, indent=2))
    print("Built", len(rows), "indexed packs:", args.output)


if __name__ == "__main__":
    main()
