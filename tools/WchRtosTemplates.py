"""Extract pinned WCH FreeRTOS ports without importing IDE metadata or binaries."""

import hashlib
import re
import zipfile


KERNEL_SOURCES = ["croutine.c", "event_groups.c", "list.c", "queue.c", "stream_buffer.c", "tasks.c", "timers.c",
                  "portable/GCC/RISC-V/port.c", "portable/GCC/RISC-V/portASM.S", "portable/MemMang/heap_4.c"]


def extract_freertos(write, archive, expected_sha256, startup_names, config_name="User/FreeRTOSConfig.h"):
    content = archive.read_bytes()
    if hashlib.sha256(content).hexdigest() != expected_sha256:
        raise RuntimeError("Official FreeRTOS archive changed; review before packaging: " + archive.name)
    prefix = "sdk/freertos"
    with zipfile.ZipFile(archive) as vendor:
        names = vendor.namelist()
        for name in names:
            if name.startswith("FreeRTOS/") and not name.endswith("/") and name != config_name and not name.endswith(".gitmodules"):
                write(prefix + "/" + name.removeprefix("FreeRTOS/"), vendor.read(name))
        for name in KERNEL_SOURCES:
            if "FreeRTOS/" + name not in names:
                raise RuntimeError("Incomplete official FreeRTOS port: " + name)
        for startup in startup_names:
            write(prefix + "/Startup/" + startup, vendor.read("Startup/" + startup))
        config = vendor.read(config_name).decode("utf-8", errors="strict").replace("\r\n", "\n").replace("\r", "\n")
        # 不依赖未知板卡串口；保留官方移植层，把应用的堆配置交给工程头文件。
        config, count = re.subn(r"(?m)^#define configTOTAL_HEAP_SIZE[^\n]*$",
            "#define configTOTAL_HEAP_SIZE ((size_t) STUDIOX_FREERTOS_HEAP_SIZE)", config)
        if count != 1:
            raise RuntimeError("Official heap configuration changed")
        config = re.sub(r"(?m)^#define configMINIMAL_STACK_SIZE[^\n]*$", "#define configMINIMAL_STACK_SIZE ((unsigned short) 128)", config)
        config = re.sub(r"(?m)^#define configUSE_TIMERS[^\n]*$", "#define configUSE_TIMERS 0", config)
        config = re.sub(r"(?m)^#define INCLUDE_xTimerPendFunctionCall[^\n]*$", "#define INCLUDE_xTimerPendFunctionCall 0", config)
        # 独立、16 字节对齐的 ISR 栈；无需改写普通模板的链接符号。
        config = config.replace("#define FREERTOS_CONFIG_H", "#define FREERTOS_CONFIG_H\n#define configISR_STACK_SIZE_WORDS 128", 1)
        config = re.sub(r"(?m)^#define configASSERT[^\n]*$",
            "#define configASSERT(x) do { if (!(x)) { taskDISABLE_INTERRUPTS(); for (;;) { } } } while (0)", config)
        write("templates/freertos/FreeRTOSConfig.h", config)
        # Archive source notices are retained verbatim. The upstream licence is included when supplied.
        if "LICENSE" in names:
            write("licenses/FreeRTOS-upstream-LICENSE", vendor.read("LICENSE"))
        kernel = vendor.read("FreeRTOS/include/FreeRTOS.h").decode(errors="replace")
        version = re.search(r"FreeRTOS Kernel V([^\s]+)", kernel)
        write("licenses/FreeRTOS-source-NOTICE.txt", kernel[:kernel.index("*/") + 2])
    return dict(archive=archive.name, sha256=expected_sha256, kernelVersion=version[1] if version else "see source notices",
                license="FreeRTOS kernel MIT notices and WCH port/source restrictions retained; original config notices retained.")


def freertos_template(entry, startup, description, heap_bytes, extra_sources=None):
    root = "sdk/freertos"
    includes = [root + "/include", root + "/portable/GCC/RISC-V"]
    if startup.startswith("startup_ch32"):
        includes.append(root + "/portable/GCC/RISC-V/chip_specific_extensions/RV32I_PFIC_no_extensions")
    return dict(id="spl-freertos", displayName="标准库 + FreeRTOS", entryFile=entry,
                description=description + "；WCH 官方 FreeRTOS 移植，双任务计数，无板级 GPIO/串口；仅离线编译验证。",
                files={"include/FreeRTOSConfig.h": "templates/freertos/FreeRTOSConfig.h"},
                build=dict(defines=["STUDIOX_FREERTOS_HEAP_SIZE=" + str(heap_bytes)], includeDirectories=includes,
                           sources=[root + "/" + path for path in KERNEL_SOURCES] + [root + "/Startup/" + startup] + (extra_sources or []),
                           compileOptions=[], linkOptions=[]))


def plain_template(entry, description, startup):
    return dict(id="spl", displayName="标准库 · 精简 main", entryFile=entry, description=description,
                build=dict(defines=[], includeDirectories=[], sources=[startup], compileOptions=[], linkOptions=[]))


def fetch_ch592(destination):
    """Download only the official port, with a pinned Git tree and verified blob identities."""
    import concurrent.futures
    import json
    import urllib.request
    from pathlib import Path

    commit = "a46e0086f1ffb5e5502703970bff94888e67f4cb"
    prefix = "EVT/EXAM/FreeRTOS/"
    headers = {"User-Agent": "MCU-StudioX-source-pin"}
    def get(url):
        with urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30) as response:
            return response.read()
    tree = json.loads(get("https://api.github.com/repos/openwch/ch592/git/trees/" + commit + "?recursive=1"))
    if tree.get("truncated") or tree.get("sha") != commit:
        raise RuntimeError("Incomplete or mismatched official source tree")
    rows = [row for row in tree["tree"] if row["type"] == "blob" and
            (row["path"] == "LICENSE" or row["path"].startswith(prefix + "FreeRTOS/") or row["path"].startswith(prefix + "Startup/") or
             row["path"] in [prefix + "Ld/Link.ld", prefix + "readme.txt", prefix + "src/main.c"])]
    def fetch(row):
        path = row["path"]
        content = get("https://raw.githubusercontent.com/openwch/ch592/" + commit + "/" + path)
        if hashlib.sha1(b"blob " + str(len(content)).encode() + b"\0" + content).hexdigest() != row["sha"]:
            raise RuntimeError("Git source hash mismatch: " + path)
        return ("LICENSE" if path == "LICENSE" else path.removeprefix(prefix), content)
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
        files = list(pool.map(fetch, rows))
    destination = Path(destination)
    if destination.exists():
        raise RuntimeError("Source archive already exists: " + str(destination))
    destination.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(destination, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for name, content in sorted(files):
            entry = zipfile.ZipInfo(name, date_time=(2026, 9, 26, 0, 0, 0))
            entry.compress_type = zipfile.ZIP_DEFLATED
            archive.writestr(entry, content)
    print(destination, hashlib.sha256(destination.read_bytes()).hexdigest())


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(__doc__)
    parser.add_argument("--fetch-ch592", required=True, help="New source-only archive path (40 small files, about 300 KiB)")
    fetch_ch592(parser.parse_args().fetch_ch592)
