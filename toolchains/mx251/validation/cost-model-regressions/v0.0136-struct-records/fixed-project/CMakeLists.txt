cmake_minimum_required(VERSION 3.25)
project(MX251_FIXED_ACCEPTANCE NONE)
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)
target_sources(app INTERFACE
  "identity.c"
  "sum_bytes.c"
  "copy_xor.c"
  "ring_buffer.c"
  "state_machine.c"
  "frame_parser.c"
  "struct_records.c"
  "crc16.c"
  "call_chain.c"
  "mix4.c"
  "main.c"
  "workloads.h"
)
set_source_files_properties("identity.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("sum_bytes.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("copy_xor.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("ring_buffer.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("state_machine.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("frame_parser.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("struct_records.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("crc16.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("call_chain.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("mix4.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_source_files_properties("main.c" PROPERTIES MX251_BACKEND "explicit-wide")
mx251_firmware(app)
