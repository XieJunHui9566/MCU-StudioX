cmake_minimum_required(VERSION 3.25)
project(MX251_FIXED_ACCEPTANCE NONE)
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)
target_sources(app INTERFACE
  "main.c"
  "callers.c"
  "callees.c"
  "wide-ordinary-calls.h"
)
set_source_files_properties("main.c" PROPERTIES MX251_BACKEND "legacy")
set_source_files_properties("callers.c" PROPERTIES MX251_BACKEND "ordinary-wide")
set_source_files_properties("callees.c" PROPERTIES MX251_BACKEND "ordinary-wide")
mx251_firmware(app)
