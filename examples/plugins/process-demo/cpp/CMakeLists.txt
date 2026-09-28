cmake_minimum_required(VERSION 3.25)
project(studiox_cpp_overview LANGUAGES CXX)

# 开发者提供本机已安装的 JSON 库；构建脚本不会联网下载依赖。
find_package(nlohmann_json 3.11 REQUIRED)
add_executable(studiox-cpp-overview main.cpp)
target_compile_features(studiox-cpp-overview PRIVATE cxx_std_20)
target_link_libraries(studiox-cpp-overview PRIVATE nlohmann_json::nlohmann_json)
if(MSVC)
  target_compile_options(studiox-cpp-overview PRIVATE /utf-8)
endif()
