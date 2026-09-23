"""Build StudioX GD32 packs from pinned GigaDevice SDK archives and Keil DFP metadata.

This maintainer recipe never accesses hardware or downloads files. The official
GitHub archive and local PDSC must already be present and match sources.json.
"""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
RECIPE = ROOT / "examples/packs/gigadevice.gd32"
SOURCE_LOCK = json.loads((RECIPE / "sources.json").read_text(encoding="utf-8"))
FAMILIES = ("GD32C10x", "GD32E10x", "GD32E23x", "GD32E50x", "GD32F10x", "GD32F1x0",
            "GD32F20x", "GD32F30x", "GD32F3x0", "GD32F403", "GD32F4xx", "GD32L23x")


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def put(stage, relative, data):
    path = stage / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(data if isinstance(data, bytes) else data.encode("utf-8"))


class Sdk:
    def __init__(self, key, folder):
        self.key = key
        info = SOURCE_LOCK[key]
        self.archive = folder / info["archive"]
        if sha256(self.archive) != info["sha256"]:
            raise ValueError(f"Official SDK archive changed: {self.archive}")
        self.zip = zipfile.ZipFile(self.archive)
        self.prefix = self.zip.namelist()[0].split("/")[0] + "/"
        if info["commit"] not in self.prefix:
            raise ValueError(f"SDK commit mismatch: {self.archive}")
        self.entries = {name[len(self.prefix):]: name for name in self.zip.namelist()
                        if name.startswith(self.prefix) and not name.endswith("/")}
        self.device_root = f"Firmware/CMSIS/GD/{key}/"
        self.driver_root = f"Firmware/{key}_standard_peripheral/"

    def read(self, relative):
        return self.zip.read(self.entries[relative])

    def files(self, prefix, suffix=None):
        return sorted(n for n in self.entries if n.startswith(prefix) and
                      (suffix is None or n.lower().endswith(suffix.lower())))


class E50xSdk:
    """Read the two SHA-locked 7z files from the official GD32 download center."""
    def __init__(self, folder):
        self.key = "GD32E50x"
        info = SOURCE_LOCK[self.key]
        self.temp = tempfile.TemporaryDirectory(prefix="studiox-gd32e50x-")
        work = Path(self.temp.name)
        seven_zip = shutil.which("7z") or shutil.which("7z.exe")
        if not seven_zip:
            raise FileNotFoundError("7-Zip 7z.exe is required for the official GD32E50x archives")

        def extract(archive, destination):
            subprocess.run([seven_zip, "x", str(archive), "-o" + str(destination), "-y"],
                           stdout=subprocess.DEVNULL, check=True)

        sdk_archive = folder / info["archive"]
        addon_archive = folder / info["addonArchive"]
        if sha256(sdk_archive) != info["sha256"] or sha256(addon_archive) != info["addonSha256"]:
            raise ValueError("Official GD32E50x SDK/AddOn archive hash mismatch")
        extract(sdk_archive, work / "sdk-outer")
        inner = work / "sdk-outer/GD32E50x_Firmware_Library_V1.7.0/GD32E50x_Firmware_Library_V1.7.0.7z"
        if sha256(inner) != info["innerSha256"]:
            raise ValueError("Official GD32E50x inner SDK hash mismatch")
        extract(inner, work / "sdk")
        self.root = work / "sdk/GD32E50x_Firmware_Library_V1.7.0"
        self.entries = {p.relative_to(self.root).as_posix(): p for p in self.root.rglob("*") if p.is_file()}
        # This vendor release omitted the GCC CMSIS compiler shim. Pin the
        # compatible Apache-2.0 header from Arm's CMSIS_5 5.2.0 tag.
        cmsis_gcc = folder / info["cmsisGccFile"]
        if sha256(cmsis_gcc) != info["cmsisGccSha256"]:
            raise ValueError("Arm CMSIS GCC header hash mismatch")
        self.entries["Firmware/CMSIS/cmsis_gcc.h"] = cmsis_gcc
        cmsis_mpu = folder / info["cmsisMpuFile"]
        if sha256(cmsis_mpu) != info["cmsisMpuSha256"]:
            raise ValueError("Arm CMSIS MPU header hash mismatch")
        self.entries["Firmware/CMSIS/mpu_armv8.h"] = cmsis_mpu
        self.device_root = "Firmware/CMSIS/GD/GD32E50x/"
        self.driver_root = "Firmware/GD32E50x_standard_peripheral/"

        extract(addon_archive, work / "addon-outer")
        addon_inner = work / "addon-outer/GD32E50x_AddOn_v1.9.0.7z"
        extract(addon_inner, work / "addon")
        dfp_pack = work / "addon/GD32E50x_AddOn_v1.9.0/GigaDevice.GD32E50x_DFP.1.9.0.pack"
        if sha256(dfp_pack) != info["dfpPackSha256"]:
            raise ValueError("Official GD32E50x DFP pack hash mismatch")
        with zipfile.ZipFile(dfp_pack) as pack:
            self.pdsc = work / "GigaDevice.GD32E50x_DFP.pdsc"
            self.pdsc.write_bytes(pack.read(self.pdsc.name))

    def read(self, relative):
        return self.entries[relative].read_bytes()

    def files(self, prefix, suffix=None):
        return sorted(n for n in self.entries if n.startswith(prefix) and
                      (suffix is None or n.lower().endswith(suffix.lower())))

    def close(self):
        self.temp.cleanup()


def pdsc_devices(path):
    root = ET.parse(path).getroot()
    family = root.find("./devices/family")
    if family is None:
        raise ValueError(f"No DFP family: {path}")
    for sub in family.findall("subFamily"):
        for device in sub.findall("device"):
            name = device.get("Dname")
            memory = {int(m.get("start"), 0): int(m.get("size"), 0)
                      for m in device.findall("memory")}
            if not name or 0x08000000 not in memory or 0x20000000 not in memory:
                raise ValueError(f"Incomplete DFP device: {name}")
            def inherited(tag, attribute):
                for node in (device, sub, family):
                    child = node.find(tag)
                    if child is not None and child.get(attribute) is not None:
                        return child.get(attribute)
                return None
            yield dict(id=name, subfamily=sub.get("DsubFamily"),
                       flash=memory[0x08000000], ram=memory[0x20000000],
                       extraRam=memory.get(0x10000000, 0),
                       core=inherited("processor", "Dcore"),
                       fpu=(inherited("processor", "Dfpu") or "0").strip() == "1",
                       defines=(inherited("compile", "define") or "").split())


def internal_clock(system):
    """Select the vendor's existing internal oscillator branch, with no board crystal assumption."""
    text = system.decode("latin1")
    lines = text.splitlines(keepends=True)
    active = [i for i, line in enumerate(lines)
              if re.match(r"^\s*#define\s+__SYSTEM_CLOCK_", line)]
    if not active:
        raise ValueError("Vendor SystemInit has no selectable clock")
    for i in active:
        lines[i] = lines[i].replace("#define", "//#define", 1)
    choices = ("__SYSTEM_CLOCK_8M_IRC8M", "__SYSTEM_CLOCK_16M_IRC16M",
               "__SYSTEM_CLOCK_IRC8M", "__SYSTEM_CLOCK_IRC16M",
               "__SYSTEM_CLOCK_48M_PLL_IRC8M")
    for choice in choices:
        matches = [i for i, line in enumerate(lines)
                   if re.match(r"^\s*//\s*#define\s+" + choice + r"\b", line)]
        if matches:
            for i in matches:
                lines[i] = re.sub(r"//\s*#define", "#define", lines[i], count=1)
            return "".join(lines).encode("latin1"), choice
    raise ValueError("No vendor internal oscillator branch")


def arm_vectors(source):
    text = source.decode("latin1")
    start = re.search(r"(?m)^__Vectors\s+DCD\s+", text)
    end = re.search(r"(?m)^__Vectors_End\b", text)
    if start is None or end is None or end.start() <= start.start():
        raise ValueError("ARMASM vector table changed")
    vectors = re.findall(r"(?m)^\s*(?:__Vectors\s+)?DCD\s+([A-Za-z_][A-Za-z0-9_]*|0)\b",
                         text[start.start():end.start()])
    if len(vectors) < 32 or vectors[:2] != ["__initial_sp", "Reset_Handler"]:
        raise ValueError("ARMASM vector table incomplete")
    return vectors


def gnu_startup_from_armasm(vectors, source_name, cpu="cortex-m3"):
    """Translate only the vendor vector order; startup runtime follows CMSIS GCC ABI."""
    rows = ["/* StudioX GNU startup; vector order transcribed from official " + source_name + ". */",
            ".syntax unified", ".cpu " + cpu, ".thumb",
            '.section .vectors,"a",%progbits', ".global __gVectors", "__gVectors:"]
    rows.extend("    .word " + ("_sp" if v == "__initial_sp" else v) for v in vectors)
    rows += ['.section .text.Reset_Handler,"ax",%progbits', ".global Reset_Handler",
             ".thumb_func", "Reset_Handler:", "    ldr r0, =_sidata", "    ldr r1, =_sdata",
             "    ldr r2, =_edata", "1:", "    cmp r1, r2", "    bcs 2f",
             "    ldr r3, [r0]", "    str r3, [r1]", "    adds r0, #4", "    adds r1, #4",
             "    b 1b", "2:", "    ldr r1, =_sbss", "    ldr r2, =_ebss", "3:",
             "    cmp r1, r2", "    bcs 4f", "    movs r3, #0", "    str r3, [r1]",
             "    adds r1, #4", "    b 3b", "4:", "    bl SystemInit",
             "    bl __libc_init_array", "    bl main", "5:  b 5b",
             '.section .text.Default_Handler,"ax",%progbits', ".thumb_func",
             "Default_Handler:", "6:  b 6b"]
    for name in dict.fromkeys(vectors):
        if name not in ("__initial_sp", "Reset_Handler", "0"):
            rows += [".weak " + name, ".thumb_set " + name + ",Default_Handler"]
    return "\n".join(rows) + "\n"


def linker(device):
    flash, ram = device["flash"], device["ram"]
    # Main SRAM only. Separate CCM/TCRAM is intentionally left unused by the default template.
    reserve_stack = 512 if ram <= 8192 else 2048
    reserve_heap = 128 if ram <= 8192 else 1024
    if reserve_stack + reserve_heap >= ram:
        raise ValueError(f"RAM too small for safe startup: {device['id']}")
    return f'''/* StudioX layout from GigaDevice DFP memory for {device['id']}. */
ENTRY(Reset_Handler)
MEMORY {{
  FLASH (rx) : ORIGIN = 0x08000000, LENGTH = {flash}
  RAM (rwx) : ORIGIN = 0x20000000, LENGTH = {ram}
}}
_sp = ORIGIN(RAM) + LENGTH(RAM);
SECTIONS {{
  .vectors : {{ . = ALIGN(4); KEEP(*(.vectors)); KEEP(*(.isr_vector)); . = ALIGN(4); }} > FLASH
  .text : {{ . = ALIGN(4); *(.text .text*); *(.rodata .rodata*); KEEP(*(.init)); KEEP(*(.fini)); . = ALIGN(4); }} > FLASH
  .ARM.extab : {{ *(.ARM.extab* .gnu.linkonce.armextab.*) }} > FLASH
  .ARM.exidx : {{ __exidx_start = .; *(.ARM.exidx*); __exidx_end = .; }} > FLASH
  .preinit_array : {{ PROVIDE_HIDDEN(__preinit_array_start = .); KEEP(*(.preinit_array*)); PROVIDE_HIDDEN(__preinit_array_end = .); }} > FLASH
  .init_array : {{ PROVIDE_HIDDEN(__init_array_start = .); KEEP(*(SORT(.init_array.*))); KEEP(*(.init_array*)); PROVIDE_HIDDEN(__init_array_end = .); }} > FLASH
  .fini_array : {{ PROVIDE_HIDDEN(__fini_array_start = .); KEEP(*(SORT(.fini_array.*))); KEEP(*(.fini_array*)); PROVIDE_HIDDEN(__fini_array_end = .); }} > FLASH
  _sidata = LOADADDR(.data);
  .data : {{ . = ALIGN(4); _sdata = .; *(.data .data*); . = ALIGN(4); _edata = .; }} > RAM AT > FLASH
  .bss (NOLOAD) : {{ . = ALIGN(4); _sbss = .; __bss_start__ = .; *(.bss .bss* COMMON); . = ALIGN(4); _ebss = .; __bss_end__ = .; }} > RAM
  .heap_stack (NOLOAD) : {{ . = ALIGN(8); PROVIDE(end = .); PROVIDE(_end = .); . = . + {reserve_heap}; . = . + {reserve_stack}; . = ALIGN(8); }} > RAM
  ASSERT(ADDR(.heap_stack) + SIZEOF(.heap_stack) <= _sp, "GD32 RAM overflow")
}}
'''


def startup_name(sdk, device):
    gcc = sdk.files(sdk.device_root + "Source/GCC/startup_", ".S")
    part = device["id"]
    if sdk.key == "GD32F30x":
        variant = "xd" if "GD32F30X_XD" in device["defines"] else "cl" if part.startswith("GD32F307") else "hd"
        return next(n for n in gcc if n.lower().endswith("_" + variant + ".s"))
    if sdk.key == "GD32F4xx":
        model = part[5:8]
        group = "405_425" if model in ("405", "425") else "407_427" if model in ("407", "427") else "450_470"
        return next(n for n in gcc if n.lower().endswith(group + ".s"))
    if sdk.key == "GD32L23x":
        return next(n for n in gcc if n.lower().endswith("l233.s"))
    if gcc:
        if len(gcc) != 1:
            raise ValueError(f"Ambiguous startup: {sdk.key}")
        return gcc[0]
    # Current official F10x/F20x releases only include ARMASM startup. The
    # generated GAS file retains their exact vector ordering and weak handlers.
    arm = sdk.files(sdk.device_root + "Source/ARM/startup_", ".s")
    if sdk.key == "GD32F10x":
        suffix = next((s for s in ("cl", "xd", "hd", "md")
                       if any(s.upper() in d for d in device["defines"])), None)
        if suffix is None:
            raise ValueError(f"Missing F10x density define: {part}")
        return next(n for n in arm if n.lower().endswith("_" + suffix + ".s"))
    if sdk.key == "GD32F20x":
        return next(n for n in arm if n.lower().endswith("_cl.s"))
    if sdk.key == "GD32E50x":
        suffix = ("gd32eprt" if part.startswith("GD32EPRT") else
                  "gd32e508" if part.startswith("GD32E508") else
                  "gd32e50x_hd" if part.startswith("GD32E503") else "gd32e50x_cl")
        return next(n for n in arm if n.lower().endswith("startup_" + suffix + ".s"))
    raise ValueError(f"No GNU startup: {sdk.key}")


def build_pack(key, sdk, devices, pdsc, output, cli):
    pack_id = "gigadevice." + key.lower()
    folder = output / pack_id
    stage = folder / "source"
    stage.mkdir(parents=True)
    core = sdk.files("Firmware/CMSIS/", ".h")
    core = [n for n in core if n.count("/") == 2]
    headers = sdk.files(sdk.device_root + "Include/", ".h")
    peripherals = sdk.files(sdk.driver_root + "Include/", ".h")
    drivers = sdk.files(sdk.driver_root + "Source/", ".c")
    system = sdk.files(sdk.device_root + "Source/system_", ".c")
    if len(system) != 1 or not headers or not peripherals or not core:
        raise ValueError(f"Incomplete SDK: {key}")
    system_bytes, clock = internal_clock(sdk.read(system[0]))
    for item in core:
        put(stage, "sdk/cmsis/" + Path(item).name, sdk.read(item))
    for item in headers:
        put(stage, "sdk/device/" + Path(item).name, sdk.read(item))
    for item in peripherals:
        put(stage, "sdk/peripheral/include/" + Path(item).name, sdk.read(item))
    for item in drivers:
        put(stage, "sdk/peripheral/src/" + Path(item).name, sdk.read(item))
    put(stage, "sdk/system/" + Path(system[0]).name, system_bytes)
    libopt = f"Template/{key.lower()}_libopt.h"
    if libopt in sdk.entries:
        put(stage, "sdk/template/" + Path(libopt).name, sdk.read(libopt))
    put(stage, "templates/main.c", f'''#include <stdint.h>
#include "{key.lower()}.h"

volatile uint32_t app_counter;
int main(void)
{{
    /* 内部时钟、无引脚假设；加入外设前请检查所选型号与实际电路。 */
    for (;;) {{ ++app_counter; __NOP(); }}
}}
''')
    put(stage, "support/runtime.c", (ROOT / "examples/packs/st.stm32f407zg/support/runtime.c").read_bytes())
    if "LICENSE" in sdk.entries:
        put(stage, "licenses/GigaDevice-LICENSE", sdk.read("LICENSE"))
    else:
        agreement = "SOFTWARE LICENSE AGREEMENT SLA-GD0001-version1.1.pdf"
        if agreement not in sdk.entries:
            raise ValueError(f"Missing vendor SDK license: {key}")
        put(stage, "licenses/" + agreement, sdk.read(agreement))
    if "Firmware/CMSIS/LICENSE.TXT" in sdk.entries:
        put(stage, "licenses/CMSIS-LICENSE.TXT", sdk.read("Firmware/CMSIS/LICENSE.TXT"))
    put(stage, "vendor/source.pdsc", pdsc.read_bytes())
    startup_paths = {}
    vector_audit = {}
    manifests = []
    for d in devices:
        original = startup_name(sdk, d)
        if original not in startup_paths:
            filename = Path(original).name.lower()
            target = "sdk/startup/" + filename.replace(".s", ".S")
            if "/GCC/" in original:
                put(stage, target, sdk.read(original))
            else:
                vectors = arm_vectors(sdk.read(original))
                put(stage, target, gnu_startup_from_armasm(vectors, Path(original).name,
                                                           d["core"].lower()))
                vector_audit[target] = dict(original=original, originalSha256=hashlib.sha256(sdk.read(original)).hexdigest(),
                                            vectorCount=len(vectors), vectors=vectors)
            startup_paths[original] = target
        put(stage, f"linker/{d['id']}.ld", linker(d))
        core_name = d["core"].lower().replace("cortex-", "cortex-")
        if core_name not in ("cortex-m3", "cortex-m4", "cortex-m23", "cortex-m33"):
            raise ValueError(f"Unsupported core {d['core']}: {d['id']}")
        flags = ["-mcpu=" + core_name, "-mthumb"]
        if d["fpu"]:
            flags += ["-mfpu=" + ("fpv5-sp-d16" if core_name == "cortex-m33" else "fpv4-sp-d16"),
                      "-mfloat-abi=hard"]
        defines = list(dict.fromkeys(d["defines"]))
        if key == "GD32E50x":
            # The official SDK examples use uppercase X, unlike the DFP's x;
            # E508 also has a distinct vendor header/startup branch.
            defines = ["GD32E50X", "GD32EPRT" if d["id"].startswith("GD32EPRT") else
                       "GD32E508" if d["id"].startswith("GD32E508") else
                       "GD32E50X_HD" if d["id"].startswith("GD32E503") else "GD32E50X_CL",
                       "USE_STDPERIPH_DRIVER"]
        if key == "GD32E10x":
            # The official header requires a nominal HXTAL_VALUE even when the
            # selected SystemInit path runs entirely from internal IRC8M.
            defines.append("HXTAL_VALUE=8000000U")
        if key == "GD32F20x":
            defines.append("GD32F20X_CL")
        if key == "GD32L23x":
            defines.append("GD32L233")
        source_names = [startup_paths[original], "sdk/system/" + Path(system[0]).name,
                        "support/runtime.c"]
        # Only drivers shared by every density are built by default. All vendor
        # peripheral source files remain available under device/ for opt-in use.
        for module in ("rcu", "gpio", "misc"):
            source = f"sdk/peripheral/src/{key.lower()}_{module}.c"
            if (stage / source).exists():
                source_names.append(source)
        manifests.append(dict(id=d["id"], displayName=f"{d['id']} · {d['flash']//1024}K Flash / {d['ram']//1024}K SRAM",
            architecture="arm", flashOrigin=0x08000000, flashBytes=d["flash"],
            ramOrigin=0x20000000, ramBytes=d["ram"], toolsetId="arm.gnu", toolsetVersion="1.0.0",
            compilerId="arm-gnu-15.2.rel1", cpuFlags=flags, defines=defines,
            includeDirectories=["sdk/cmsis", "sdk/device", "sdk/peripheral/include", "sdk/template"],
            sources=source_names, linkerScript=f"linker/{d['id']}.ld",
            compileOptions=["-Os", "-g3", "-ffunction-sections", "-fdata-sections", "-fno-common"],
            linkOptions=["-nostartfiles", "--specs=nano.specs", "--specs=nosys.specs", "-Wl,--gc-sections"],
            templates=[dict(id="spl", displayName="标准外设库 · 内部时钟最小工程",
                description=f"{d['flash']//1024} KiB Flash / {d['ram']//1024} KiB 主 SRAM；默认使用厂商内部时钟分支 {clock}，不假设板上 LED 或晶振。",
                entryFile="templates/main.c")]))
    manifest = dict(formatVersion=1, id=pack_id, version="0.1.0", vendor="GigaDevice",
                    displayName=key + " · 标准外设库", devices=manifests)
    put(stage, "manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    put(stage, "catalog.json", json.dumps(devices, ensure_ascii=False, indent=2))
    if vector_audit:
        put(stage, "vendor/vectors.json", json.dumps(vector_audit, ensure_ascii=False, indent=2))
    source_info = SOURCE_LOCK[sdk.key]
    provenance = dict(sdkSource=source_info["url"], sdkArchiveSha256=source_info["sha256"],
        dfpSource="GigaDevice " + pdsc.name, dfpSha256=sha256(pdsc),
        generatedFromArmAsm=vector_audit, clockPatch=f"vendor SystemInit selects {clock}",
        license="Original GigaDevice/CMSIS license files retained")
    if "commit" in source_info:
        provenance["sdkCommit"] = source_info["commit"]
    else:
        provenance.update(sdkVersion=source_info["releaseVersion"],
                          dfpVersion=source_info["dfpVersion"],
                          dfpSourceUrl=source_info["addonUrl"],
                          dfpArchiveSha256=source_info["addonSha256"],
                          dfpPackSha256=source_info["dfpPackSha256"],
                          cmsisGccSource=source_info["cmsisGccUrl"],
                          cmsisGccSha256=source_info["cmsisGccSha256"],
                          cmsisMpuSource=source_info["cmsisMpuUrl"],
                          cmsisMpuSha256=source_info["cmsisMpuSha256"])
    put(stage, "provenance.json", json.dumps(provenance, ensure_ascii=False, indent=2))
    put(stage, "README.md", (RECIPE / "README.md").read_bytes())
    archive = folder / (pack_id + "-0.1.0.mcupack")
    subprocess.run(["dotnet", str(cli), "pack", str(stage), str(archive)], check=True)
    return dict(file=archive.relative_to(output).as_posix(), id=pack_id, version="0.1.0", series=key,
                devices=[d["id"] for d in devices],
                sha256=sha256(archive), bytes=archive.stat().st_size)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, required=True, help="Previously downloaded official GitHub ZIP archives")
    parser.add_argument("--pdsc", type=Path, required=True, help="GigaDevice Keil DFP .pdsc directory")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    parser.add_argument("--family", choices=FAMILIES, action="append", help="Limit to one or more families")
    parser.add_argument("--append", action="store_true", help="Resume generation in an existing output directory")
    args = parser.parse_args()
    if args.output.exists() and not args.append:
        raise ValueError("Output exists; use a new destination")
    args.output.mkdir(parents=True, exist_ok=args.append)
    cli = ROOT / "src/StudioX.Cli/bin/Debug/net10.0/StudioX.Cli.dll"
    index = args.output / "index.json"
    results = json.loads(index.read_text(encoding="utf-8")) if index.exists() else []
    for key in args.family or FAMILIES:
        if any(entry["series"] == key for entry in results):
            continue
        sdk = E50xSdk(args.sources) if key == "GD32E50x" else Sdk(key, args.sources)
        try:
            pdsc_key = "GD32F4xx" if key == "GD32F403" else key
            pdsc = sdk.pdsc if key == "GD32E50x" else args.pdsc / f"GigaDevice.{pdsc_key}_DFP.pdsc"
            devices = list(pdsc_devices(pdsc))
            if key == "GD32F403":
                devices = [d for d in devices if d["id"].startswith("GD32F403")]
            elif key == "GD32F4xx":
                devices = [d for d in devices if not d["id"].startswith("GD32F403")]
            elif key == "GD32E50x":
                # EPRT is a separate special-purpose series. Its DFP base
                # names do not unambiguously match today's orderable SKUs.
                devices = [d for d in devices if not d["id"].startswith("GD32EPRT")]
            if not devices:
                raise ValueError(f"No DFP devices: {key}")
            results.append(build_pack(key, sdk, devices, pdsc, args.output, cli))
            print(f"{key}: {len(devices)} devices, {results[-1]['sha256']}", flush=True)
        finally:
            if isinstance(sdk, E50xSdk):
                sdk.close()
    put(args.output, "index.json", json.dumps(results, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
