cmake_minimum_required(VERSION 3.20.0)

# 本工程自带实验板定义；BOARD_ROOT 指向包含 boards/ 的工程根目录。
list(APPEND BOARD_ROOT "${CMAKE_CURRENT_SOURCE_DIR}")
find_package(Zephyr 4.4.2 EXACT REQUIRED HINTS "$ENV{ZEPHYR_BASE}")
project(alientek_explorer_f407zg_blinky)

target_sources(app PRIVATE src/main.c)
