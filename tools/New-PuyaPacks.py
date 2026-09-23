"""Build StudioX PY32 packs from pinned Puya-source community mirrors; no network or hardware use."""

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
RECIPE = ROOT / "examples/packs/puya.py32-series"
VERSION = "0.1.1"
PINNED = {
    "PY32F0xx": "ae83041d2ed88a0eace4fb4ab522a41c83882a92",
    "PY32F002B": "394627d99085c5eae72142503b0405b84428bb20",
    "PY32F005": "831636eb2013eddbc2a8d9fd2b06aded5d1ea917",
    "PY32F040": "3a3e586720fcca21d59ecb04bc9cf3c95261cd6e",
    "PY32F071": "0ed2f4b4d3391eccfd4491006a30295fd78e32c2",
    "PY32F072": "0b646fb7705f3c1f29de27e613c047ddd0cdf770",
}

# 每项对应一份厂商固件库。F002A、F003、F030 共用 F0xx 库，但生成独立包。
FAMILIES = {
    "F002A": ("PY32F0xx", "PY32F002xx_Templates", "py32f002ax5.ld"),
    "F002B": ("PY32F002B", "PY32F002Bxx_Templates", "py32f002bx5.ld"),
    "F003": ("PY32F0xx", "PY32F003xx_Templates", "py32f003x8.ld"),
    "F005": ("PY32F005", "PY32F005xx_Templates", None),
    "F030": ("PY32F0xx", "PY32F030xx_Templates", "py32f030x8.ld"),
    "F040": ("PY32F040", None, "py32f040xb.ld"),
    "F071": ("PY32F071", "PY32F071xx_Templates", "py32f071xb.ld"),
    "F072": ("PY32F072", "PY32F072xx_Templates", "py32f072xb.ld"),
}


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


def source_layout(repo, family, template_name):
    if template_name:
        template = repo / "Templates" / template_name
    else:
        # F040 镜像没有模板目录；选择库内官方裸机工程中的 GCC 文件。
        template = repo / "Projects/PY32F040-STK/Example/WWDG/WWDG_Window"
    system = next((template / "Src").glob("system_py32*.c"))
    if family in {"F040", "F071", "F072"}:
        # The HAL example's system.c uses RCC_SYSCLKSOURCE_STATUS_* names
        # defined only by HAL RCC. The vendor's LL example uses device-register
        # RCC_CFGR_SWS_* values, so it also builds for a plain CMSIS project.
        system = (repo / "Projects" / ("PY32" + family + "-STK") /
                  "Example_LL/WWDG/WWDG_WINDOW/Src" / ("system_py32f" + family[1:].lower() + ".c"))
        if not system.is_file():
            raise FileNotFoundError(system)
    startup = next((template / "EIDE").glob("startup_py32*.s"), None)
    device_dir = next((repo / "Drivers/CMSIS/Device").iterdir()) / "Include"
    driver_dir = next(d for d in (repo / "Drivers").iterdir() if d.name.endswith("HAL_Driver"))
    config = next((template / "Inc").glob("py32f*_hal_conf.h"))
    return system, startup, device_dir, driver_dir, config, template


def f005_startup(pack):
    """Translate the official F005 ARMASM vector list into a GCC C startup.

    Puya's F005 SDK has no GCC startup; all vector names/order come from its DFP.
    """
    with zipfile.ZipFile(pack) as archive:
        source = archive.read("Drivers/CMSIS/Device/PY32F0xx/Source/arm/startup_py32f005xx.s").decode(errors="replace")
    vector_block = re.search(r"(?ms)^__Vectors\s+DCD(.*?)^__Vectors_End", source)
    if vector_block is None:
        raise ValueError("F005 DFP vector table not found")
    vectors = re.findall(r"\bDCD\s+([A-Za-z_][A-Za-z_0-9]*|0)\s*;", "DCD" + vector_block.group(1))
    if len(vectors) != 35 or vectors[0:2] != ["__initial_sp", "Reset_Handler"]:
        raise ValueError("F005 DFP vector list changed; inspect startup before rebuilding")
    handlers = sorted(set(vectors) - {"__initial_sp", "Reset_Handler", "0"})
    declarations = "\n".join(f"void {h}(void) __attribute__((weak, alias(\"Default_Handler\")));" for h in handlers)
    entries = ",\n    ".join("(uintptr_t)&_estack" if x == "__initial_sp" else "0" if x == "0" else f"(uintptr_t){x}" for x in vectors)
    return f'''/* F005 vector names/order: Puya DFP ARMASM startup; StudioX GCC translation. */
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
    repo_name, template_name, linker_name = FAMILIES[family]
    repo = source_root / (repo_name + "_Firmware")
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
    system, startup, device_dir, driver_dir, config, template = source_layout(repo, family, template_name)
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
    if family == "F005":
        write(stage / "sdk/startup/startup_py32f005xx.c", f005_startup(pack))
        startup_rel = "sdk/startup/startup_py32f005xx.c"
        # 原厂 F005 镜像缺 GCC linker，沿用原厂 F002B GCC 段布局，再以 F005 DFP 覆写内存边界。
        base = source_root / "PY32F002B_Firmware/Templates/PY32F002Bxx_Templates/EIDE/py32f002bx5.ld"
        linker_note = "Puya F002B GCC section layout; F005 memory from Puya DFP 1.2.10"
    else:
        copy(startup, stage / "sdk/startup" / startup.name)
        startup_rel = "sdk/startup/" + startup.name
        base = template / "EIDE" / linker_name
        linker_note = str(base.relative_to(repo)).replace("\\", "/")
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
        # Puya DFP 1.2.6 收录 F003x7，但同版 SDK 缺其寄存器头/选择分支；不可虚报可编译。
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
                      linkerSource=linker_note,
                      systemSource=system.relative_to(repo).as_posix(),
                      systemSourceNote=("Vendor Example_LL source avoids HAL RCC-only macros in CMSIS mode"
                                        if family in {"F040", "F071", "F072"} else "Vendor template source"),
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
