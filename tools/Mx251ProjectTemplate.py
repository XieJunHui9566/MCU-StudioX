"""Shared readable CMake scaffold for StudioX MX251 device packs."""


def render(ai8051u: bool) -> str:
    text = '''cmake_minimum_required(VERSION 3.25)

# 用户构建配置：在下面添加源码、头文件目录与宏。
# 目标、内存模型与运行库配置保存在 mx251.json。
project(mx251_firmware_project LANGUAGES NONE)

set(MX251_FUNCTION_GC ON CACHE BOOL "Remove unreachable functions")
set(MX251_WIDE_VALUE_LIMIT 512 CACHE STRING "Wide values")
set(MX251_WIDE_NODE_LIMIT 2048 CACHE STRING "Wide nodes")

# 固定的 MX251 构建入口，由所选工具链提供。
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)

# 1. 用户源码：每行一个文件。
target_sources(app INTERFACE
    src/main.c
    # src/uart.c
)

# 2. 用户头文件目录。
target_include_directories(app INTERFACE
    include
)

# 3. 用户宏：填写宏名或 宏名=值，不需要 -D。
target_compile_definitions(app INTERFACE
    # USE_UART=1
)

'''
    if ai8051u:
        text += '''# 4. 库函数模板：在 COMPONENTS 中添加所需驱动。
if(EXISTS "${CMAKE_SOURCE_DIR}/include/library_config.h")
    include("${CMAKE_SOURCE_DIR}/device/sdk/stc/cmake/MX251StcSdk.cmake")

    # 此值用于库函数时间计算，必须与下载配置中的实际系统时钟一致。
    set(STC_CLOCK_HZ 40000000 CACHE STRING "Application system clock in Hz")
    mx251_stc_sdk(app
        TARGET AI8051U-34K64-32
        CLOCK_HZ "${STC_CLOCK_HZ}"
        COMPONENTS
            GPIO
    )
    mx251_stc_firmware(app)
else()
    mx251_firmware(app)
endif()
'''
    else:
        text += '# 4. 生成固件。\nmx251_firmware(app)\n'
    return text
