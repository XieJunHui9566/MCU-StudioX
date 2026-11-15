cmake_minimum_required(VERSION 3.25)
project(MX251_FIXED_ACCEPTANCE NONE)
include("${MX251_PROJECT_MODULE}")
add_library(app INTERFACE)
target_sources(app INTERFACE
  "ring.c"
  "crc.c"
  "parser.c"
  "mutate.c"
  "legacy.c"
  "app.c"
  "unused.c"
  "main.c"
  "app.h"
  "fixtures.h"
)
set_source_files_properties("ring.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "ring.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_source_files_properties("crc.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "crc.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_source_files_properties("parser.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "parser.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_source_files_properties("mutate.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "mutate.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_source_files_properties("legacy.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "legacy.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_source_files_properties("app.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "app.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_source_files_properties("unused.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "unused.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_source_files_properties("main.c" PROPERTIES MX251_BACKEND "explicit-wide")
set_property(SOURCE "main.c" PROPERTY COMPILE_DEFINITIONS "USE_NATIVE")
set_property(SOURCE "ring.c" PROPERTY MX251_ARCHIVE "app_library")
set_property(SOURCE "crc.c" PROPERTY MX251_ARCHIVE "app_library")
set_property(SOURCE "parser.c" PROPERTY MX251_ARCHIVE "app_library")
set_property(SOURCE "mutate.c" PROPERTY MX251_ARCHIVE "app_library")
set_property(SOURCE "legacy.c" PROPERTY MX251_ARCHIVE "app_library")
set_property(SOURCE "app.c" PROPERTY MX251_ARCHIVE "app_library")
set_property(SOURCE "unused.c" PROPERTY MX251_ARCHIVE "app_library")
mx251_firmware(app)
