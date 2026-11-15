cmake_minimum_required(VERSION 3.25)
project(MX251_FIXED_ACCEPTANCE NONE)
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)
target_sources(app INTERFACE
  "kernel0.c"
  "kernel1.c"
  "kernel2.c"
  "kernel3.c"
  "kernel4.c"
  "kernel5.c"
  "kernel6.c"
  "kernel7.c"
  "kernel8.c"
  "kernel9.c"
  "kernel10.c"
  "kernel11.c"
  "kernel12.c"
  "kernel13.c"
  "kernel14.c"
  "kernel15.c"
  "kernel16.c"
  "kernel17.c"
  "bench_board.c"
  "bench_all.h"
)
set_source_files_properties("kernel0.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel1.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel2.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel3.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel4.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel5.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel6.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel7.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel8.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel9.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel10.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel11.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel12.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel13.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel14.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel15.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel16.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("kernel17.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("bench_board.c" PROPERTIES MX251_BACKEND "explicit-wide")
mx251_firmware(app)
