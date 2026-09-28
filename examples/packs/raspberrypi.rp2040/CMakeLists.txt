# 仅供包作者从固定版本 SDK 提取实际依赖；用户工程使用已解析的源码包。
cmake_minimum_required(VERSION 3.24)
include("${PICO_SDK_PATH}/external/pico_sdk_import.cmake")
project(studiox_rp2040_sdk LANGUAGES C CXX ASM)
pico_sdk_init()
add_executable(firmware minimal.c)
target_link_libraries(firmware PRIVATE
    pico_stdlib pico_multicore pico_rand pico_unique_id
    hardware_adc hardware_dma hardware_flash hardware_i2c hardware_interp
    hardware_pio hardware_pwm hardware_rtc hardware_spi
)
pico_enable_stdio_uart(firmware 0)
pico_enable_stdio_usb(firmware 0)
pico_add_extra_outputs(firmware)
