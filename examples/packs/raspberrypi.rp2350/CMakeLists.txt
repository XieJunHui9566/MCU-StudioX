# 仅供包作者从固定版本 SDK 提取实际源码和编译/链接依赖；用户工程不执行此文件。
cmake_minimum_required(VERSION 3.24)
include(${PICO_SDK_PATH}/external/pico_sdk_import.cmake)
project(studiox_rp2350_sdk LANGUAGES C CXX ASM)
pico_sdk_init()
add_executable(firmware minimal.c)
target_link_libraries(firmware PRIVATE
    pico_stdlib pico_multicore pico_rand pico_unique_id
    hardware_adc hardware_dma hardware_flash hardware_i2c hardware_interp
    hardware_pio hardware_powman hardware_pwm hardware_sha256 hardware_spi
)
pico_enable_stdio_uart(firmware 0)
pico_enable_stdio_usb(firmware 0)
pico_add_extra_outputs(firmware)
