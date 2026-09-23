namespace StudioX.Application.CodeIntelligence;

internal sealed record CMakeCommand(string Name, string Signature, string Description, string[] Parameters);

/// <summary>离线常用命令目录，采用 CMake 3.24 已有语法；保持与最低工程版本一致。</summary>
internal static class CMakeCatalog
{
    public static readonly IReadOnlyDictionary<string, CMakeCommand> Commands = new[]
    {
        Command("cmake_minimum_required", "VERSION <min> [FATAL_ERROR]", "声明最低 CMake 版本。例：cmake_minimum_required(VERSION 3.24)", "VERSION", "最低版本，如 3.24"),
        Command("project", "<name> [VERSION <version>] [LANGUAGES <languages>...]", "定义工程与编程语言。例：project(firmware LANGUAGES C CXX ASM)", "工程名称", "可选 VERSION / LANGUAGES；单片机通常使用 C CXX ASM"),
        Command("set", "<variable> <value>... [CACHE <type> <docstring> [FORCE] | PARENT_SCOPE]", "设置变量。例：set(CMAKE_C_STANDARD 17)", "变量名（这里不加 ${}）", "变量值；列表可用空格或分号分隔"),
        Command("option", "<variable> <help_text> [value]", "定义布尔缓存选项。例：option(USE_UART \"Enable UART\" ON)", "选项名称", "引号括起的说明", "默认值：ON / OFF"),
        Command("unset", "<variable> [CACHE | PARENT_SCOPE]", "取消变量或缓存项。", "变量名", "可选 CACHE / PARENT_SCOPE"),
        Command("add_executable", "<name> [WIN32] [MACOSX_BUNDLE] [EXCLUDE_FROM_ALL] <sources>...", "创建可执行目标。例：add_executable(firmware src/main.c)", "新目标名称", "源文件路径或 ${源文件变量}"),
        Command("add_library", "<name> [STATIC | SHARED | MODULE | OBJECT | INTERFACE] <sources>...", "创建库目标。单片机静态库示例：add_library(drivers STATIC src/driver.c)", "新目标名称", "库类型或源文件；单片机常用 STATIC"),
        Command("target_sources", "<target> <PRIVATE|PUBLIC|INTERFACE> <sources>...", "追加源文件。例：target_sources(firmware PRIVATE src/uart.c)", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "源文件路径或 ${变量}"),
        Command("target_include_directories", "<target> [SYSTEM] [AFTER|BEFORE] <PRIVATE|PUBLIC|INTERFACE> <dirs>...", "添加头文件搜索目录。例：target_include_directories(firmware PRIVATE device/sdk/include)", "已定义的目标", "可见性；PRIVATE 仅当前目标，PUBLIC 同时传递，INTERFACE 仅传递", "头文件目录；可重复可见性分组"),
        Command("target_compile_definitions", "<target> <PRIVATE|PUBLIC|INTERFACE> <definitions>...", "添加预处理宏，无需写 -D。例：target_compile_definitions(firmware PRIVATE USE_UART=1)", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "宏名或 宏名=值，例如 USE_UART=1"),
        Command("target_compile_options", "<target> [BEFORE] <PRIVATE|PUBLIC|INTERFACE> <options>...", "给目标添加编译选项。例：target_compile_options(firmware PRIVATE -Wall)", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "编译选项；CPU/ABI 应与器件工具链一致"),
        Command("target_compile_features", "<target> <PRIVATE|PUBLIC|INTERFACE> <features>...", "声明所需语言特性。例：target_compile_features(firmware PRIVATE c_std_17)", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "语言特性，例如 c_std_17 / cxx_std_17"),
        Command("target_link_libraries", "<target> <PRIVATE|PUBLIC|INTERFACE> <items>...", "链接库或其他目标。例：target_link_libraries(firmware PRIVATE drivers)", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "库目标、库名称或文件路径"),
        Command("target_link_options", "<target> [BEFORE] <PRIVATE|PUBLIC|INTERFACE> <options>...", "设置链接阶段选项。例：target_link_options(firmware PRIVATE -Wl,--gc-sections)", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "链接选项，例如 -Wl,--gc-sections"),
        Command("target_link_directories", "<target> [BEFORE] <PRIVATE|PUBLIC|INTERFACE> <dirs>...", "设置库搜索目录。可用目标名链接时优先使用 target_link_libraries。", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "库目录路径"),
        Command("target_precompile_headers", "<target> <PRIVATE|PUBLIC|INTERFACE> <headers>...", "设置预编译头文件。", "已定义的目标", "可见性：PRIVATE / PUBLIC / INTERFACE", "头文件路径"),
        Command("set_target_properties", "<targets>... PROPERTIES <prop> <value>...", "设置目标属性。例：set_target_properties(firmware PROPERTIES SUFFIX .elf)", "目标列表", "PROPERTIES 后交替填写属性名与值"),
        Command("get_target_property", "<variable> <target> <property>", "读取目标属性到变量。", "接收结果的变量", "目标名称", "属性名称"),
        Command("set_property", "<scope> <names>... [APPEND] PROPERTY <name> <values>...", "设置作用域中的属性。", "作用域，例如 TARGET / SOURCE / DIRECTORY", "对象列表和 PROPERTY <属性名> <值>"),
        Command("add_subdirectory", "<source_dir> [binary_dir] [EXCLUDE_FROM_ALL]", "加入含 CMakeLists.txt 的子目录。例：add_subdirectory(drivers)", "源目录", "可选构建目录或 EXCLUDE_FROM_ALL"),
        Command("include", "<file|module> [OPTIONAL] [RESULT_VARIABLE <var>] [NO_POLICY_SCOPE]", "加载 .cmake 文件或模块。例：include(cmake/options.cmake)", "文件路径或模块名称", "可选 OPTIONAL / RESULT_VARIABLE / NO_POLICY_SCOPE"),
        Command("find_package", "<PackageName> [version] [QUIET] [REQUIRED] [COMPONENTS <components>...]", "查找依赖包。", "包名称", "版本及 REQUIRED / QUIET / COMPONENTS 等选项"),
        Command("find_path", "<variable> NAMES <names>... [PATHS <paths>...]", "查找包含指定文件的目录。", "保存目录的变量名", "NAMES 文件名；PATHS 搜索路径"),
        Command("find_library", "<variable> NAMES <names>... [PATHS <paths>...]", "查找库文件。", "保存结果的变量名", "NAMES 库名；PATHS 搜索路径"),
        Command("find_program", "<variable> NAMES <names>... [PATHS <paths>...]", "查找可执行程序。", "保存结果的变量名", "NAMES 程序名；PATHS 搜索路径"),
        Command("add_custom_command", "TARGET <target> <PRE_BUILD|PRE_LINK|POST_BUILD> COMMAND <command> [args]... [VERBATIM]", "定义自定义构建步骤，也支持 OUTPUT 形式。例：add_custom_command(TARGET firmware POST_BUILD COMMAND ${CMAKE_SIZE} $<TARGET_FILE:firmware> VERBATIM)", "TARGET 或 OUTPUT", "目标名或输出文件", "构建时机 / COMMAND / 参数 / VERBATIM"),
        Command("add_custom_target", "<name> [ALL] [COMMAND <command> [args]...] [DEPENDS <dependencies>...]", "定义自定义构建目标。", "新目标名称", "COMMAND / DEPENDS / WORKING_DIRECTORY 等选项"),
        Command("add_dependencies", "<target> <dependencies>...", "声明目标之间的构建顺序。", "已定义的目标", "依赖目标名称"),
        Command("configure_file", "<input> <output> [COPYONLY] [@ONLY]", "从模板生成配置文件。例：configure_file(config.h.in config.h @ONLY)", "输入模板路径", "输出文件路径", "可选 COPYONLY / @ONLY"),
        Command("file", "<subcommand> <arguments>...", "文件操作。例：file(GLOB APP_SOURCES CONFIGURE_DEPENDS src/*.c)；明确列出源文件更便于维护。", "子命令，例如 GLOB / READ / WRITE / COPY / MAKE_DIRECTORY", "子命令对应的参数"),
        Command("list", "<subcommand> <list> [arguments]...", "列表操作。例：list(APPEND APP_SOURCES src/main.c)", "子命令，例如 APPEND / REMOVE_ITEM / LENGTH", "列表变量名（这里不加 ${}）", "子命令对应的值或输出变量"),
        Command("string", "<subcommand> <arguments>...", "字符串操作。", "子命令，例如 REPLACE / APPEND / TOUPPER", "子命令对应的参数"),
        Command("message", "[<mode>] <message>...", "输出配置日志。例：message(STATUS \"Building ${PROJECT_NAME}\")", "可选 STATUS / WARNING / FATAL_ERROR 等等级", "消息内容；通常使用引号"),
        Command("if", "<condition>", "条件分支。例：if(USE_UART)；以 endif() 结束。", "条件：变量、DEFINED、TARGET、STREQUAL、AND / OR / NOT 等"),
        Command("elseif", "<condition>", "追加条件分支。", "条件表达式"),
        Command("else", "", "开始其他条件分支。"),
        Command("endif", "", "结束 if 条件块。"),
        Command("foreach", "<variable> IN <LISTS lists|ITEMS items>...", "遍历列表，也支持 RANGE。", "循环变量名", "IN LISTS / IN ITEMS / RANGE 或元素列表"),
        Command("endforeach", "", "结束 foreach 循环。"),
        Command("while", "<condition>", "条件循环。", "条件表达式"),
        Command("endwhile", "", "结束 while 循环。"),
        Command("function", "<name> [arguments]...", "定义具有独立变量作用域的函数。", "函数名称", "形式参数名"),
        Command("endfunction", "", "结束 function 定义。"),
        Command("macro", "<name> [arguments]...", "定义在调用方作用域展开的宏。", "宏名称", "形式参数名"),
        Command("endmacro", "", "结束 macro 定义。"),
        Command("return", "", "从当前文件或函数返回。"),
        Command("break", "", "退出当前循环。"),
        Command("continue", "", "继续下一次循环。"),
        Command("enable_language", "<languages>...", "启用额外的编程语言。", "C / CXX / ASM 等语言"),
        Command("enable_testing", "", "启用测试。"),
        Command("add_test", "NAME <name> COMMAND <command> [args]...", "注册测试命令。", "NAME", "测试名", "COMMAND 及参数"),
        Command("install", "<TARGETS|FILES|DIRECTORY|...> <items>... [DESTINATION <dir>]", "声明安装规则。", "安装内容类型", "安装对象及 DESTINATION 目录"),
        Command("include_guard", "[DIRECTORY|GLOBAL]", "防止同一配置文件重复包含。", "可选 DIRECTORY / GLOBAL")
    }.ToDictionary(item => item.Name, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyDictionary<string, string> Variables = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["PROJECT_NAME"] = "最近一次 project() 的工程名", ["PROJECT_SOURCE_DIR"] = "当前工程源码目录", ["PROJECT_BINARY_DIR"] = "当前工程构建目录",
        ["CMAKE_SOURCE_DIR"] = "顶层源码目录", ["CMAKE_BINARY_DIR"] = "顶层构建目录", ["CMAKE_CURRENT_SOURCE_DIR"] = "当前处理的源码目录",
        ["CMAKE_CURRENT_BINARY_DIR"] = "当前处理的构建目录", ["CMAKE_CURRENT_LIST_DIR"] = "当前 CMake 文件所在目录", ["CMAKE_CURRENT_LIST_FILE"] = "当前 CMake 文件完整路径",
        ["CMAKE_BUILD_TYPE"] = "单配置构建类型：Debug / Release / RelWithDebInfo / MinSizeRel", ["CMAKE_C_STANDARD"] = "C 语言标准，例如 11 / 17",
        ["CMAKE_CXX_STANDARD"] = "C++ 语言标准，例如 17 / 20", ["CMAKE_C_STANDARD_REQUIRED"] = "是否强制指定的 C 标准", ["CMAKE_CXX_STANDARD_REQUIRED"] = "是否强制指定的 C++ 标准",
        ["CMAKE_C_EXTENSIONS"] = "是否允许 C 编译器扩展", ["CMAKE_CXX_EXTENSIONS"] = "是否允许 C++ 编译器扩展", ["CMAKE_EXPORT_COMPILE_COMMANDS"] = "输出 compile_commands.json（支持的生成器）",
        ["CMAKE_TOOLCHAIN_FILE"] = "工具链文件路径", ["CMAKE_SYSTEM_NAME"] = "目标系统；裸机常用 Generic", ["CMAKE_SYSTEM_PROCESSOR"] = "目标处理器标识",
        ["CMAKE_TRY_COMPILE_TARGET_TYPE"] = "试编译目标类型；裸机通常为 STATIC_LIBRARY", ["CMAKE_C_COMPILER"] = "C 编译器", ["CMAKE_CXX_COMPILER"] = "C++ 编译器",
        ["CMAKE_ASM_COMPILER"] = "汇编编译器", ["CMAKE_C_FLAGS"] = "全局 C 编译选项", ["CMAKE_CXX_FLAGS"] = "全局 C++ 编译选项", ["CMAKE_ASM_FLAGS"] = "全局汇编选项",
        ["CMAKE_EXE_LINKER_FLAGS"] = "全局可执行目标链接选项", ["CMAKE_OBJCOPY"] = "二进制格式转换工具", ["CMAKE_OBJDUMP"] = "反汇编工具", ["CMAKE_SIZE"] = "固件体积工具（由工具链定义）",
        ["CMAKE_AR"] = "归档工具", ["CMAKE_COMMAND"] = "当前 CMake 可执行程序", ["CMAKE_MODULE_PATH"] = "附加 CMake 模块搜索目录", ["CMAKE_PREFIX_PATH"] = "依赖包搜索前缀",
        ["CMAKE_INSTALL_PREFIX"] = "安装根目录", ["BUILD_SHARED_LIBS"] = "未指定类型时是否构建共享库"
    };

    public static readonly string[] Properties = ["C_STANDARD", "C_STANDARD_REQUIRED", "C_EXTENSIONS", "CXX_STANDARD", "CXX_STANDARD_REQUIRED", "CXX_EXTENSIONS", "OUTPUT_NAME", "PREFIX", "SUFFIX", "LINKER_LANGUAGE", "POSITION_INDEPENDENT_CODE", "RUNTIME_OUTPUT_DIRECTORY", "ARCHIVE_OUTPUT_DIRECTORY", "COMPILE_DEFINITIONS", "INCLUDE_DIRECTORIES", "LINK_OPTIONS"];
    public static readonly string[] Scopes = ["PRIVATE", "PUBLIC", "INTERFACE"];
    private static CMakeCommand Command(string name, string arguments, string description, params string[] parameters) => new(name, name + "(" + arguments + ")", description, parameters);
}
