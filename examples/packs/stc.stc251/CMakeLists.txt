cmake_minimum_required(VERSION 3.24)
project(firmware_project LANGUAGES NONE)
include("${MX251_CMAKE_MODULE}")

# CMake 负责文件、包含目录和宏；MX251 适配层负责目标编译与链接。
add_library(app INTERFACE)
target_sources(app INTERFACE
    src/main.c
    src/AI8051U_Delay.c
)
target_include_directories(app INTERFACE include)
# target_compile_definitions(app INTERFACE MY_FEATURE=1)

# 延迟例程单独按需链接，未调用时不增加固件体积。
set_source_files_properties(src/AI8051U_Delay.c PROPERTIES
    MX251_BACKEND legacy
    MX251_ARCHIVE device_support
)
mx251_firmware(app)
