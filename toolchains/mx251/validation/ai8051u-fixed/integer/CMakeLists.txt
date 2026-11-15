cmake_minimum_required(VERSION 3.25)
project(MX251_FIXED_ACCEPTANCE NONE)
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)
target_sources(app INTERFACE
  "main.c"
  "functions.c"
  "native-int.h"
)
set_source_files_properties("main.c" PROPERTIES MX251_BACKEND "legacy")
set_source_files_properties("functions.c" PROPERTIES MX251_BACKEND "legacy")
mx251_firmware(app)
