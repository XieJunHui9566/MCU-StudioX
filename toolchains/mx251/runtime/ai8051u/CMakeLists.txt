cmake_minimum_required(VERSION 3.25)
project(MX251_AI8051U_Runtime NONE)
if(NOT CMAKE_GENERATOR STREQUAL "Ninja")
  message(FATAL_ERROR "MX251 runtime requires Ninja")
endif()
if(NOT EXISTS "${MX251_TOOLCHAIN_ROOT}/bin/sdas251.exe" OR NOT EXISTS "${MX251_TOOLCHAIN_ROOT}/bin/sdar.exe")
  message(FATAL_ERROR "Pass the project-locked MX251_TOOLCHAIN_ROOT; no global tool lookup")
endif()
add_custom_command(OUTPUT "${CMAKE_BINARY_DIR}/mdu32.rel"
  COMMAND "${MX251_TOOLCHAIN_ROOT}/bin/sdas251.exe" -plosgff mdu32.rel "${CMAKE_CURRENT_SOURCE_DIR}/mdu32.asm"
  DEPENDS mdu32.asm "${MX251_TOOLCHAIN_ROOT}/bin/sdas251.exe"
  WORKING_DIRECTORY "${CMAKE_BINARY_DIR}" VERBATIM)
add_custom_command(OUTPUT "${CMAKE_BINARY_DIR}/libai8051umdu32.lib"
  COMMAND "${CMAKE_COMMAND}" -E rm -f "${CMAKE_BINARY_DIR}/libai8051umdu32.lib"
  COMMAND "${MX251_TOOLCHAIN_ROOT}/bin/sdar.exe" -rcD libai8051umdu32.lib mdu32.rel
  DEPENDS "${CMAKE_BINARY_DIR}/mdu32.rel" "${MX251_TOOLCHAIN_ROOT}/bin/sdar.exe"
  WORKING_DIRECTORY "${CMAKE_BINARY_DIR}" VERBATIM)
add_custom_target(runtime ALL DEPENDS "${CMAKE_BINARY_DIR}/libai8051umdu32.lib")
