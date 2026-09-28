# SPDX-License-Identifier: Apache-2.0

# 休眠中的 STM32F407 需要经 NRST 附加；OpenOCD 初始化时保持复位，
# GDB 连接后执行 reset halt 才释放复位，避免服务端提前 halt 超时。
board_runner_args(openocd --no-halt --gdb-init "monitor reset halt")
# 复位保持时 GDB 初次读到的寄存器缓存为零；释放复位后立即刷新。
board_runner_args(openocd --gdb-init "maintenance flush register-cache")

# 只声明 runner；运行调试、下载仍需用户在 IDE 中明确选择。
include(${ZEPHYR_BASE}/boards/common/openocd.board.cmake)
