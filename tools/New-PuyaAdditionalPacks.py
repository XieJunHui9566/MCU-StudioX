"""Build additional StudioX PY32 packs from pinned community mirrors; offline only."""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / "examples/packs/puya.py32-additional"
VERSION = "0.1.0"
PINNED = {
    "PY32F031": "96600bee4f613e2971e260aa7f5e2a55a724b04e",
    "PY32F032": "36cb236dc170000e7d4fa8b5605710e40c76bde7",
    "PY32F033": "6b3cdfbafbb20b7082aba04746fcde391f57f8b0",
    "PY32F090": "87aa05ad89958a07207acc5d8e1b0dacd820bbe2",
    "PY32F092": "5e88e346b0ede55f422ee99d8547304709301b7a",
}

FAMILIES = {"F031": "PY32F031x8", "F032": "PY32F032x8",
            "F033": "PY32F033x8", "F090": "PY32F090xB", "F092": "PY32F092xC"}


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, content):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(content, encoding="utf-8", newline="\n")


def copy(source, target):
    target.parent.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        shutil.copytree(source, target)
    else:
        shutil.copy2(source, target)


def check_source(path, name):
    if not path.is_dir():
        raise FileNotFoundError(f"Missing {name} mirror: {path}")
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=path, text=True).strip()
    if actual != PINNED[name]:
        raise ValueError(f"{name} commit changed: {actual}")
    dirty = subprocess.check_output(["git", "status", "--porcelain"], cwd=path, text=True).strip()
    if dirty:
        raise ValueError(f"{name} mirror is modified")


def pdsc_devices(pack, family):
    with zipfile.ZipFile(pack) as archive:
        name = next(n for n in archive.namelist() if n.endswith(".pdsc"))
        root = ET.fromstring(archive.read(name))
        sub = next(s for s in root.findall("./devices/family/subFamily") if s.get("DsubFamily") == "PY32" + family)
        svd = sub.find("debug").get("svd")
        result = []
        for node in sub.findall("device"):
            memories = {int(m.get("start"), 0): int(m.get("size"), 0) for m in node.findall("memory")}
            result.append((node.get("Dname"), memories[0x08000000], memories[0x20000000]))
        return result, archive.read(svd), name


def source_layout(repo, family):
    template = repo / "Templates" / ("PY32" + family + "xx_Templates")
    ll_template = repo / "Templates" / ("PY32" + family + "xx_Templates_LL")
    # The vendor LL system.c uses device-register constants and works without
    # enabling HAL headers in a plain CMSIS project.
    system = next((ll_template / "Src").glob("system_py32*.c"))
    startup = next((template / "EIDE").glob("startup_py32*.s"), None)
    device_dir = repo / "Drivers/CMSIS/Device" / ("PY32" + family) / "Include"
    driver_dir = next(d for d in (repo / "Drivers").iterdir() if d.name.endswith("HAL_Driver"))
    config = next((template / "Inc").glob("py32f*_hal_conf.h"))
    return system, startup, device_dir, driver_dir, config, template


def translated_startup(pack, family):
    """Translate the Puya DFP ARMASM vector names/order into GCC C startup."""
    with zipfile.ZipFile(pack) as archive:
        source = archive.read("Drivers/CMSIS/Device/PY32F0xx/Source/arm/startup_py32" + family.lower() + "xx.s").decode(errors="replace")
    vector_block = re.search(r"(?ms)^__Vectors\s+DCD(.*?)^__Vectors_End", source)
    if vector_block is None:
        raise ValueError(f"{family} DFP vector table not found")
    vectors = re.findall(r"\bDCD\s+([A-Za-z_][A-Za-z_0-9]*|0)\s*;", "DCD" + vector_block.group(1))
    if len(vectors) != 48 or vectors[0:2] != ["__initial_sp", "Reset_Handler"]:
        raise ValueError(f"{family} DFP vector list changed; inspect startup before rebuilding")
    handlers = sorted(set(vectors) - {"__initial_sp", "Reset_Handler", "0"})
    declarations = "\n".join(f"void {h}(void) __attribute__((weak, alias(\"Default_Handler\")));" for h in handlers)
    entries = ",\n    ".join("(uintptr_t)&_estack" if x == "__initial_sp" else "0" if x == "0" else f"(uintptr_t){x}" for x in vectors)
    return f'''/* {family} vector names/order: Puya DFP ARMASM startup; StudioX GCC translation. */
#include <stdint.h>
extern uint32_t _estack, _sidata, _sdata, _edata, _sbss, _ebss;
extern void SystemInit(void);
extern void __libc_init_array(void);
extern int main(void);
void Reset_Handler(void);
void Default_Handler(void) {{ for (;;) {{ __asm volatile ("nop"); }} }}
{declarations}
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
'''


def build_one(family, source_root, output, cli):
    repo_name = "PY32" + family
    repo = source_root / (repo_name + "_Firmware")
    if not repo.is_dir() and family == "F031":
        repo = source_root / (repo_name + "_Firmware-retry")
    check_source(repo, repo_name)
    package_id = "puya.py32" + family.lower()
    archive = output / (package_id + "-" + VERSION + ".mcupack")
    if archive.is_file():
        existing = output / ("source-py32" + family.lower()) / "manifest.json"
        if not existing.is_file():
            raise ValueError(f"Archive exists without source manifest: {archive}")
        devices = json.loads(existing.read_text(encoding="utf-8"))["devices"]
        return dict(file=archive.name, id=package_id, version=VERSION, devices=[d["id"] for d in devices], sha256=sha(archive))
    pack = next((repo / "Packs/MDK").glob("*.pack"))
    devices, svd, pdsc_name = pdsc_devices(pack, family)
    system, startup, device_dir, driver_dir, config, template = source_layout(repo, family)
    stage = output / ("source-py32" + family.lower())
    if stage.exists():
        # 上次构建中断时保留现场，另建 staging；不删除用户文件。
        stage = output / (stage.name + "-retry")
        if stage.exists():
            raise FileExistsError(f"Refusing to overwrite {stage}")
    stage.mkdir(parents=True)
    copy(repo / "Drivers/CMSIS/Include", stage / "sdk/cmsis")
    copy(device_dir, stage / "sdk/device")
    copy(driver_dir / "Inc", stage / "sdk/driver/inc")
    copy(driver_dir / "Src", stage / "sdk/driver/src")
    copy(system, stage / "sdk/system/system.c")
    copy(ROOT / "examples/packs/st.stm32f407zg/support/runtime.c", stage / "support/runtime.c")
    copy(config, stage / "sdk/config" / config.name)
    copy(repo / "LICENSE", stage / "licenses/Puya-BSD-3-Clause.txt")
    write(stage / "svd" / ("PY32" + family + ".svd"), svd.decode("utf-8-sig"))
    if startup:
        copy(startup, stage / "sdk/startup" / startup.name)
        startup_rel = "sdk/startup/" + startup.name
        startup_note = startup.relative_to(repo).as_posix()
        base = next((template / "EIDE").glob("py32f*.ld"))
        linker_note = str(base.relative_to(repo)).replace("\\", "/")
    else:
        startup_name = "startup_py32" + family.lower() + "xx.c"
        write(stage / "sdk/startup" / startup_name, translated_startup(pack, family))
        startup_rel = "sdk/startup/" + startup_name
        startup_note = "Puya DFP Drivers/CMSIS/Device/PY32F0xx/Source/arm/startup_py32" + family.lower() + "xx.s; StudioX C/GCC conversion"
        # Newer vendor mirrors have no GCC project. Reuse the vendor F032 GCC
        # section layout while the target DFP sets exact memory boundaries.
        base_repo = source_root / "PY32F032_Firmware"
        check_source(base_repo, "PY32F032")
        base = base_repo / "Templates/PY32F032xx_Templates/EIDE/py32f032x8.ld"
        linker_note = "Puya F032 GCC section layout at " + PINNED["PY32F032"] + "; target memory from DFP"
    # Vendor *_template.c files are optional example timebases, not HAL library
    # implementations; compiling them as normal sources needs extra modules.
    hal_files = sorted(p for p in (stage / "sdk/driver/src").glob("*_hal*.c")
                       if not p.name.endswith("_template.c"))
    ll_files = sorted((stage / "sdk/driver/src").glob("*_ll*.c"))
    if not hal_files or not ll_files:
        raise ValueError(f"Missing HAL/LL sources: {family}")
    hal_header = next((stage / "sdk/driver/inc").glob("*_hal.h")).name
    for mode in ("cmsis", "hal", "ll"):
        content = (RECIPE / ("main-" + mode + ".c")).read_text(encoding="utf-8").replace("@HAL_HEADER@", hal_header)
        write(stage / "templates" / ("main-" + mode + ".c"), content)
    catalog = []
    manifest_devices = []
    for name, flash, ram in devices:
        # F031 DFP has older x4/x6/x7 capacity branches. Current vendor
        # product pages/datasheet confirm only x8, so do not expose the rest.
        if name != FAMILIES[family]:
            catalog.append(dict(id=name, flashBytes=flash, ramBytes=ram,
                                status="excluded: no current Puya product confirmation"))
            continue
        header = (stage / "sdk/device/py32f0xx.h").read_text(encoding="utf-8", errors="replace")
        if "defined(" + name + ")" not in header:
            catalog.append(dict(id=name, flashBytes=flash, ramBytes=ram, status="excluded: SDK device header missing"))
            continue
        link = (stage / "linker" / (name.lower() + ".ld"))
        text = base.read_text(encoding="utf-8-sig", errors="replace")
        text, count = re.subn(r"MEMORY\s*\{.*?\}", f"MEMORY\n{{\n  RAM (xrw) : ORIGIN = 0x20000000, LENGTH = {ram}\n  FLASH (rx) : ORIGIN = 0x08000000, LENGTH = {flash}\n}}", text, count=1, flags=re.S)
        if count != 1:
            raise ValueError(f"Linker MEMORY not found: {base}")
        write(link, text)
        templates = []
        for mode in ("cmsis", "hal", "ll"):
            overlay = None
            if mode != "cmsis":
                overlay = dict(defines=["USE_HAL_DRIVER"] if mode == "hal" else ["USE_FULL_LL_DRIVER"],
                               includeDirectories=["sdk/driver/inc", "sdk/config"],
                               sources=[p.relative_to(stage).as_posix() for p in (hal_files if mode == "hal" else ll_files)],
                               compileOptions=["-Wno-unused-parameter"], linkOptions=[])
            templates.append(dict(id=mode, displayName={"cmsis": "CMSIS · 寄存器", "hal": "Puya HAL", "ll": "Puya LL"}[mode],
                                  description=f"{flash // 1024} KiB Flash / {ram // 1024} KiB SRAM；默认内部时钟；无板级引脚配置。",
                                  entryFile=f"templates/main-{mode}.c", build=overlay))
        manifest_devices.append(dict(id=name, displayName=f"{name} · {flash // 1024}K/{ram // 1024}K · 封装通配", architecture="arm",
                                     flashOrigin=0x08000000, flashBytes=flash, ramOrigin=0x20000000, ramBytes=ram,
                                     toolsetId="arm.gnu", toolsetVersion="1.0.0", compilerId="arm-gnu-15.2.rel1",
                                     cpuFlags=["-mcpu=cortex-m0plus", "-mthumb"], defines=[name],
                                     includeDirectories=["sdk/cmsis", "sdk/device"],
                                     sources=[startup_rel, "sdk/system/system.c", "support/runtime.c"], linkerScript=link.relative_to(stage).as_posix(),
                                     compileOptions=["-Os", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common", "--specs=nano.specs"],
                                     linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections", "-Wl,--no-warn-rwx-segments"],
                                     templates=templates))
        catalog.append(dict(id=name, flashBytes=flash, ramBytes=ram, status="packaged"))
    if not manifest_devices:
        raise ValueError(f"No buildable devices: {family}")
    manifest = dict(formatVersion=1, id=package_id, version=VERSION, displayName="PY32" + family + " · CMSIS / HAL / LL", vendor="Puya", devices=manifest_devices)
    write(stage / "manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    write(stage / "catalog.json", json.dumps(catalog, ensure_ascii=False, indent=2))
    write(stage / "README.md", (RECIPE / "README.md").read_text(encoding="utf-8"))
    provenance = dict(source="OpenPuya community mirror of Puya firmware release; not Puya's official GitHub organization",
                      mirror="https://github.com/OpenPuya/" + repo_name + "_Firmware",
                      official="https://www.puyasemi.com/download.html?keywords=PY32" + family,
                      commit=PINNED[repo_name], dfpFile=pack.name, dfpSha256=sha(pack), pdscPath=pdsc_name,
                      linkerSource=linker_note, startupSource=startup_note,
                      systemSource=system.relative_to(repo).as_posix(),
                      systemSourceNote="Vendor LL template source works in CMSIS/HAL/LL modes",
                      licenses=["licenses/Puya-BSD-3-Clause.txt"],
                      selectedSdkFilesSha256={p.relative_to(stage).as_posix(): sha(p) for p in sorted((stage / "sdk").rglob("*")) if p.is_file()})
    write(stage / "provenance.json", json.dumps(provenance, ensure_ascii=False, indent=2))
    subprocess.run(["dotnet", str(cli), "pack", str(stage), str(archive)], check=True)
    return dict(file=archive.name, id=package_id, version=VERSION, devices=[d["id"] for d in manifest_devices], sha256=sha(archive))


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--source-root", type=Path, required=True, help="Directory containing pinned *_Firmware mirrors")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    parser.add_argument("--family", choices=FAMILIES, help="Build one family for iteration")
    args = parser.parse_args()
    cli = ROOT / "src/StudioX.Cli/bin/Release/net10.0/StudioX.Cli.dll"
    if not cli.is_file():
        raise FileNotFoundError(f"Build StudioX.Cli Release first: {cli}")
    args.output.mkdir(parents=True, exist_ok=True)
    rows = [build_one(f, args.source_root, args.output, cli) for f in ([args.family] if args.family else FAMILIES)]
    write(args.output / "index.json", json.dumps(rows, ensure_ascii=False, indent=2))
    print(f"Built {len(rows)} Puya packs; output: {args.output}")


if __name__ == "__main__":
    main()
