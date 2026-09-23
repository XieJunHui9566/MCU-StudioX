"""Build a self-contained RP2350 C-source pack from a pinned local Pico SDK."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import shlex
import subprocess

SDK_COMMIT = "a1438dff1d38bd9c65dbd693f0e5db4b9ae91779"


def main():
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--sdk", type=Path, required=True)
    parser.add_argument("--toolset", type=Path, required=True, help="Prepared arm.gnu/1.0.0 directory")
    parser.add_argument("--python", type=Path, required=True, help="Package-author Python; not shipped in the pack")
    parser.add_argument("--output", type=Path, required=True, help="New output directory")
    args = parser.parse_args()
    repo = Path(__file__).resolve().parents[1]
    recipe = repo / "examples/packs/raspberrypi.rp2350"
    sdk, toolset = args.sdk.resolve(), args.toolset.resolve()
    revision = subprocess.check_output(["git", "-C", str(sdk), "rev-parse", "HEAD"], text=True).strip()
    if revision != SDK_COMMIT or subprocess.check_output(["git", "-C", str(sdk), "status", "--porcelain", "--untracked-files=no"], text=True).strip():
        raise RuntimeError("Use the unmodified Pico SDK 2.2.0 pinned commit " + SDK_COMMIT)
    tools = json.loads((toolset / "toolset.json").read_text(encoding="utf-8-sig"))
    if (tools["id"], tools["version"], tools["compilerId"]) != ("arm.gnu", "1.0.0", "arm-gnu-15.2.rel1"):
        raise RuntimeError("Expected StudioX arm.gnu/1.0.0")
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    build, stage = output / "sdk-build", output / "source"
    query = build / ".cmake/api/v1/query"
    query.mkdir(parents=True)
    (query / "codemodel-v2").touch()
    cmake = str(toolset / tools["executables"]["cmake"])
    ninja = str(toolset / tools["executables"]["ninja"])
    gcc_bin = (toolset / tools["executables"]["gcc"]).parent

    def run(arguments, log):
        result = subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
        (output / log).write_text(result.stdout, encoding="utf-8")
        if result.returncode:
            raise RuntimeError(f"See {output / log}\n" + result.stdout[-4000:])

    run([cmake, "-S", str(recipe), "-B", str(build), "-G", "Ninja", "-DCMAKE_MAKE_PROGRAM=" + ninja,
         "-DPICO_SDK_PATH=" + sdk.as_posix(), "-DPICO_TOOLCHAIN_PATH=" + gcc_bin.parent.as_posix(),
         "-DPython3_EXECUTABLE=" + args.python.resolve().as_posix(), "-DPICO_BOARD=pico2", "-DPICO_PLATFORM=rp2350-arm-s",
         "-DCMAKE_BUILD_TYPE=Debug", "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON", "-DPICO_NO_PICOTOOL=1"], "sdk-configure.log")
    run([cmake, "--build", str(build), "--parallel", "4"], "sdk-build.log")
    reply = build / ".cmake/api/v1/reply"
    index = json.loads(next(reply.glob("index-*.json")).read_text())
    model = json.loads((reply / index["reply"]["codemodel-v2"]["jsonFile"]).read_text())
    target_ref = next(t for t in model["configurations"][0]["targets"] if t["name"] == "firmware")
    target = json.loads((reply / target_ref["jsonFile"]).read_text())
    group = next(g for g in target["compileGroups"] if g["language"] == "C")
    originals = {}

    def write(name, data):
        path = stage / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data if isinstance(data, bytes) else data.encode("utf-8"))

    def sdk_path(path):
        if path.is_relative_to(sdk):
            return "sdk/" + path.relative_to(sdk).as_posix()
        if path.is_relative_to(build):
            return "sdk/" + path.relative_to(build).as_posix()
        raise RuntimeError("Unexpected SDK dependency outside authoring roots: " + str(path))

    def copy(path):
        name = sdk_path(path)
        data = path.read_bytes()
        if path.is_relative_to(build) and path.name == "bs2_default_padded_checksummed.S":
            data = re.sub(rb"^// Padded and checksummed version of:.*", b"// Padded and checksummed official RP2350 boot2 (Pico SDK 2.2.0).", data)
        if path.is_relative_to(sdk):
            originals[path.relative_to(sdk).as_posix()] = hashlib.sha256(data).hexdigest()
        write(name, data)
        return name

    includes = []
    for entry in group["includes"]:
        path = Path(entry["path"])
        includes.append(sdk_path(path))
        for file in path.rglob("*"):
            if file.is_file():
                # 板型显式固定，避免把无关板卡配置和用户项目混入包。
                if path == sdk / "src/boards/include" and file.name != "pico2.h":
                    continue
                copy(file)
    sources = []
    for entry in target["sources"]:
        path = Path(entry["path"])
        if not path.is_absolute():
            path = (build if entry.get("isGenerated") else recipe) / path
        if path.resolve() == recipe / "minimal.c" or path.suffix.lower() not in (".c", ".cpp", ".s"):
            continue
        sources.append(copy(path.resolve()))
    sources.append(copy(build / "pico-sdk/src/rp2350/boot_stage2/bs2_default_padded_checksummed.S"))

    # 编译器依赖还包含源文件旁的 .inc.S 等私有资源，不能只按公开 include 目录收集。
    dependencies = subprocess.check_output([ninja, "-C", str(build), "-t", "deps"], text=True, encoding="utf-8", errors="strict")
    for line in dependencies.splitlines():
        if line.startswith("    "):
            dependency = Path(line.strip()).resolve()
            if dependency.is_relative_to(sdk):
                copy(dependency)

    # 在包制作阶段预生成 boot2，最终包仍保留原始汇编/生成器及许可，供来源核查。
    for path in (sdk / "src/rp2350/boot_stage2").rglob("*"):
        if path.is_file():
            copy(path)
    for name in ["src/rp2_common/cmsis/include/cmsis/rename_exceptions.h", "LICENSE.TXT"]:
        copy(sdk / name)

    defines, string_defines = [], []
    for entry in group["defines"]:
        value = entry["define"]
        if '"' in value:
            key, literal = value.split("=", 1)
            string_defines += [f"#ifndef {key}", f"#define {key} {literal}", "#endif"]
        else:
            defines.append(value)
    config = stage / "sdk/generated/pico_base/pico/config_autogen.h"
    config.write_text("// StudioX: relocatable board/configuration snapshot of Pico SDK 2.2.0.\n" +
                      "\n".join(string_defines) + '\n#include "boards/pico2.h"\n#include "cmsis/rename_exceptions.h"\n', encoding="utf-8")
    includes.append("sdk/src/rp2_common/cmsis/include")
    linker = sdk / "src/rp2_common/pico_crt0/rp2350/memmap_default.ld"
    originals[linker.relative_to(sdk).as_posix()] = hashlib.sha256(linker.read_bytes()).hexdigest()
    original_linker = linker.read_text()
    if original_linker.count('INCLUDE "pico_flash_region.ld"') != 1:
        raise RuntimeError("Review upstream linker changes")
    write("linker/rp2350-pico2.ld", original_linker.replace('INCLUDE "pico_flash_region.ld"', 'FLASH(rx) : ORIGIN = 0x10000000, LENGTH = 4M'))
    flags = shlex.split(" ".join(f["fragment"] for f in group["compileCommandFragments"]))
    cpu_flags = [f for f in flags if f.startswith("-m")]
    link_options = []
    for fragment in target["link"]["commandFragments"]:
        if fragment["role"] != "flags":
            continue
        for flag in shlex.split(fragment["fragment"]):
            if flag.startswith(("-Wl,-Map=", "-Wl,-L", "-Wl,--script=", "-m", "-O", "-g")):
                continue
            if ":/" in flag or "\\" in flag:
                raise RuntimeError("Unexpected absolute linker flag: " + flag)
            if flag not in link_options:
                link_options.append(flag)
    write("debug/rp2350-pico2.cfg", (recipe / "rp2350-pico2.cfg").read_bytes())
    write("README.md", (recipe / "README.md").read_bytes())
    templates = []
    for id, title, description in [
        ("minimal", "Pico SDK · 最小 C 工程", "内存计数与延时，不初始化外部 GPIO。"),
        ("blink", "Pico SDK · GPIO 闪灯（C）", "默认板载 LED GP25，兼容板请按原理图修改 LED_PIN。"),
        ("multicore", "Pico SDK · 双核队列（C）", "两颗 Cortex-M33 通过 SDK 队列交换数据，不操作外部 GPIO。")]:
        write(f"templates/{id}.c", (recipe / f"{id}.c").read_bytes())
        templates.append(dict(id=id, displayName=title, entryFile=f"templates/{id}.c", description=description +
            " 默认外部 12 MHz → 150 MHz；4 MiB QSPI Flash / 520 KiB SRAM；ARM Secure。"))
    device = dict(id="RP2350A-PICO2", displayName="RP2350A · Pico 2 / 兼容板（4 MB Flash）", architecture="arm",
        flashOrigin=0x10000000, flashBytes=0x400000, ramOrigin=0x20000000, ramBytes=520 * 1024,
        toolsetId="arm.gnu", toolsetVersion="1.0.0", compilerId="arm-gnu-15.2.rel1", cpuFlags=cpu_flags,
        defines=defines, includeDirectories=list(dict.fromkeys(includes)), sources=list(dict.fromkeys(sources)),
        linkerScript="linker/rp2350-pico2.ld", compileOptions=["-Og", "-g3", "-ffunction-sections", "-fdata-sections"],
        linkOptions=link_options, templates=templates,
        openOcd=dict(targetScript="debug/rp2350-pico2.cfg", applicationFlashBytes=0x400000,
            probes=[dict(id="cmsis-dap", displayName="DAP-Link (CMSIS-DAP)", interfaceScript="interface/cmsis-dap.cfg", transport="swd", defaultSpeedKhz=1000)]))
    manifest = dict(formatVersion=1, id="raspberrypi.rp2350", version="0.1.0", displayName="RP2350 · Pico SDK C 工程", vendor="Raspberry Pi", devices=[device])
    write("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
    write("vendor/provenance.json", json.dumps(dict(upstream="https://github.com/raspberrypi/pico-sdk", version="2.2.0", commit=revision,
        compiler="arm-gnu-15.2.rel1", originalFilesSha256=originals, changes=["CMake-resolved C SDK dependency snapshot for pico2/rp2350-arm-s",
        "relocatable generated config header", "inline exact 4 MiB flash region in linker", "pre-generated official boot2 checksum assembly",
        "StudioX C templates and CMSIS-DAP target checks"], license="BSD-3-Clause and retained per-file notices; see sdk/LICENSE.TXT"), indent=2))
    # 构建工具路径只能存在于临时作者日志，不能进入交付包。
    for path in stage.rglob("*"):
        if path.is_file() and path.suffix.lower() in (".h", ".c", ".s", ".ld", ".json", ".cmake"):
            text = path.read_text(encoding="utf-8", errors="replace")
            if any(root.as_posix() in text or str(root) in text for root in [sdk, build, toolset]):
                raise RuntimeError("Developer path leaked into pack: " + str(path))
    archive = output / "raspberrypi.rp2350-0.1.0.mcupack"
    subprocess.run(["dotnet", str(repo / "src/StudioX.Cli/bin/Release/net10.0/StudioX.Cli.dll"), "pack", str(stage), str(archive)], check=True)
    (output / "index.json").write_text(json.dumps(dict(file=archive.name, sha256=hashlib.sha256(archive.read_bytes()).hexdigest(),
        sdkCommit=revision, devices=[device["id"]], templates=[t["id"] for t in templates]), indent=2), encoding="utf-8")
    print(f"Created {archive}; {len(sources)} SDK sources, {len(includes)} include directories")


if __name__ == "__main__":
    main()
