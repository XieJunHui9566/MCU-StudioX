"""读取并校验本机 AGM FreeRTOS，保留原始来源与可审查的移植差异。"""

import hashlib
import json
import re

KERNEL_SOURCES = [
    "tasks.c", "queue.c", "list.c", "timers.c", "event_groups.c",
    "stream_buffer.c", "croutine.c", "portable/MemMang/heap_4.c",
    "portable/GCC/RISC-V/port.c", "portable/GCC/RISC-V/portASM.S",
]


def prepare(directory, recipe):
    locked = json.loads((recipe / "freertos/source-lock.json").read_text(encoding="utf-8"))
    raw = {}
    for name, expected in locked["files"].items():
        data = (directory / name).read_bytes()
        if hashlib.sha256(data).hexdigest() != expected:
            raise RuntimeError("AGM FreeRTOS 输入已改变，需要重新审查：" + name)
        raw[name] = data
    files = dict(raw)
    port = "portable/GCC/RISC-V/port.c"
    text = files[port].decode("utf-8").replace("\r\n", "\n")
    text, count = re.subn(r"const size_t uxTimerIncrementsForOneTick = [^\n]+",
                         "/* StudioX: initialized from the actual clock by vPortSetupTimerInterrupt. */\n"
                         "size_t uxTimerIncrementsForOneTick = 0;", text)
    if count != 1:
        raise RuntimeError("AGM FreeRTOS 节拍变量结构改变。")
    files[port] = text.encode()
    extension = "portable/GCC/RISC-V/freertos_risc_v_chip_specific_extensions.h"
    text = files[extension].decode("utf-8").replace("\r\n", "\n")
    text = text.replace("portasmADDITIONAL_CONTEXT_SIZE  32", "portasmADDITIONAL_CONTEXT_SIZE  36")
    text = text.replace("freertos_risc_v_application_interrupt_handler handle_trap",
                        "freertos_risc_v_application_interrupt_handler StudioX_FreeRtosInterruptHandler")
    # 通用端口将 mepc 写入 0(sp)，AGM 原扩展从相同位置保存 f0，会被覆盖。
    # 留出 mepc 字，32 个 F 寄存器与 fcsr 独立保存，附加空间保持 16 字节倍数。
    text = text.replace("-FSTKSIZE", "-(36 * REGBYTES)").replace("sp, FSTKSIZE", "sp, (36 * REGBYTES)")
    text, stores = re.subn(r"(FSTORE\s+f\d+,\s*)(\d+) \* FREGBYTES",
                          lambda m: m[1] + str(int(m[2]) + 1) + " * FREGBYTES", text)
    text, loads = re.subn(r"(FLOAD\s+f\d+,\s*)(\d+) \* FREGBYTES",
                         lambda m: m[1] + str(int(m[2]) + 1) + " * FREGBYTES", text)
    if stores != 32 or loads != 32:
        raise RuntimeError("AGM 浮点上下文布局改变。")
    text = re.sub(r"(  FSTORE\s+f31,[^\n]+)", r"\1\n  frcsr   t0\n  sw      t0, 33 * REGBYTES(sp)", text)
    text = re.sub(r"(  FLOAD\s+f0,[^\n]+)", r"  lw      t0, 33 * REGBYTES(sp)\n  fscsr   t0\n\1", text)
    if "frcsr" not in text or "fscsr" not in text:
        raise RuntimeError("未能添加 fcsr 保存恢复。")
    files[extension] = text.encode()
    provenance = {
        "packageName": "framework-agrv_freertos", "packageVersion": "1.0.0",
        "kernelVersion": "11.1.0", "source": "AGM 本机 SDK 配套 FreeRTOS",
        "vendorGuide": "https://www.ag32mcu.com/dev-docs/doc_ag32_other_freertos/",
        "note": "网页描述旧版 10.4.6；实际内核 task.h 标记 V11.1.0，AGM 芯片扩展保留 V10.4.6 注释。以锁定源码为准。",
        "license": "MIT；完整 LICENSE.md 和源码注释保留。",
        "originalFiles": locked["files"],
        "derivedFiles": {name: hashlib.sha256(files[name]).hexdigest() for name in (port, extension)},
        "transformations": [
            "节拍步长改为运行期变量，系统层在启动调度器时按实际 SYSCLK 初始化，兼容 HSI 回退。",
            "浮点上下文保留 0(sp) 的 mepc，增加 fcsr 保存恢复，附加 36 字空间保持对齐。",
            "外部中断显式调用 StudioX 包装入口，避免两个弱 handle_trap 的链接顺序冲突。",
        ],
    }
    return files, provenance


def templates():
    root = "sdk/freertos"
    common = dict(
        files={"include/FreeRTOSConfig.h": "templates/freertos/FreeRTOSConfig.h"},
        build=dict(defines=["STUDIOX_FREERTOS=1"],
                   includeDirectories=[root + "/include", root + "/portable/GCC/RISC-V"],
                   sources=[root + "/" + name for name in KERNEL_SOURCES],
                   compileOptions=[], linkOptions=[]),
    )
    return [dict(common, id="freertos-mcu", displayName="FreeRTOS",
                 description="FreeRTOS 11.1.0，双任务、1 ms 节拍、32 KiB 堆；勾选下方特殊模式可加入 FPGA 内部回环。",
                 entryFile="templates/freertos/main.c", replacesTemplates=["freertos-mixed"],
                 ag32Sources=dict(pinMapFile="templates/freertos/pins.ve", verilogFile="templates/freertos/user_logic.v",
                                  entryFile="templates/freertos/main-logic.c",
                                  build=dict(defines=["STUDIOX_AG32_LOGIC_LOOPBACK=1"],
                                             includeDirectories=[], sources=[], compileOptions=[], linkOptions=[])))]
