namespace StudioX.Engine;

using System.Text;
using StudioX.Foundation;

public static class CMakeGenerator
{
    public const string ManagedMarker = "# StudioX managed device support (layout 2)";
    public const string DeviceListPath = "device/CMakeLists.txt";
    public const string PlatformPath = "device/platform.cmake";
    public static bool IsManagedFile(string relativePath, string text) =>
        (relativePath.Equals(DeviceListPath, StringComparison.OrdinalIgnoreCase) || relativePath.Equals(PlatformPath, StringComparison.OrdinalIgnoreCase)) &&
        text.StartsWith(ManagedMarker, StringComparison.Ordinal);

    /// <summary>根配置只在创建工程时生成，后续构建不会重写用户的源码、目录或宏列表。</summary>
    public static string Render(BuildPlan plan)
    {
        var template = plan.Device.Templates.Single(t => t.Id == plan.Project.TemplateId);
        var sources = (template.Files?.Keys ?? []).Where(p => Path.GetExtension(p).ToLowerInvariant() is ".c" or ".cpp" or ".cc" or ".s");
        if (plan.Device.Architecture == "mcs51")
        {
            if (sources.Any(p => !p.EndsWith(".c", StringComparison.OrdinalIgnoreCase)))
                throw new StudioXException("PROJECT_LANGUAGE", "SDCC MCS-51 工程模板只能包含 C 源码。");
            var sdcc = """
                cmake_minimum_required(VERSION 3.24)

                # 用户构建配置：在下面添加自己的 C 源码、头文件目录与宏。
                # device/ 内的寄存器定义与芯片参数由器件包管理。
                include(device/platform.cmake)
                project(firmware LANGUAGES C)
                set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

                add_executable(firmware)
                add_subdirectory(device)
                studiox_configure_firmware(firmware)

                target_sources(firmware PRIVATE
                    src/main.c
                    # src/uart.c
                )

                target_include_directories(firmware PRIVATE
                    src
                    include
                )

                target_compile_definitions(firmware PRIVATE
                    # USE_UART=1
                )
                """ + "\n";
            return sdcc.Replace("    # src/uart.c", string.Join("\n", sources.Select(p => "    " + Quote(p)).Append("    # src/uart.c")), StringComparison.Ordinal);
        }
        var content = """
            cmake_minimum_required(VERSION 3.24)

            # 用户构建配置：在下面添加自己的源码、头文件目录、宏和库。
            # device/ 内的厂商资源与配置由器件包管理，通常无需修改。
            include(device/platform.cmake)
            project(firmware LANGUAGES C CXX ASM)
            set(CMAKE_C_STANDARD 17)
            set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

            # 固定的器件支持入口，保留这三行。
            add_executable(firmware)
            add_subdirectory(device)
            studiox_configure_firmware(firmware)

            # 1. 用户源码：每行一个 .c / .cpp / .S 文件。
            target_sources(firmware PRIVATE
                src/main.c
                # src/uart.c
            )

            # 2. 用户头文件目录。
            target_include_directories(firmware PRIVATE
                src
                include
            )

            # 3. 用户宏：填写宏名或 宏名=值，不需要 -D。
            target_compile_definitions(firmware PRIVATE
                # USE_UART=1
            )

            # 4. 用户库：需要独立模块时，在这里添加 add_subdirectory(...)。
            target_link_libraries(firmware PRIVATE
                # my_library
            )
            """ + "\n";
        return content.Replace("    # src/uart.c", string.Join("\n", sources.Select(p => "    " + Quote(p)).Append("    # src/uart.c")), StringComparison.Ordinal);
    }

    public static string RenderDevice(BuildPlan plan)
    {
        var d = plan.Device;
        var text = new StringBuilder(ManagedMarker + "\n# 自动生成：厂商库、寄存器头文件、启动代码与芯片参数。\n# 添加应用代码请修改根目录 CMakeLists.txt。\n\n");
        // 通过 INTERFACE 源文件编入最终目标，避免静态归档丢弃启动向量或弱中断实现。
        text.AppendLine("add_library(studiox_device INTERFACE)");
        // 厂商静态库必须作为链接依赖而不是源码；否则 CMake 不会把 .a 加入链接命令。
        Append("target_sources", d.Sources.Where(p => !p.EndsWith(".a", StringComparison.OrdinalIgnoreCase)).Select(DevicePath), generated: true);
        Append("target_link_libraries", d.Sources.Where(p => p.EndsWith(".a", StringComparison.OrdinalIgnoreCase)).Select(DevicePath), generated: true);
        Append("target_include_directories", d.IncludeDirectories.Select(p => DevicePath(p)), generated: true);
        Append("target_compile_definitions", d.Defines);
        // 同一接口同时传递到应用与厂商源码；CPU/ABI 在编译和链接阶段保持一致。
        Append("target_compile_options", d.CpuFlags.Concat(d.CompileOptions));
        if (d.Architecture == "mcs51")
            // CMake 会去重独立选项元素；--iram-size 256 和 --xram-size 256 的两个 256
            // 若不分组，后一项会被删掉。SHELL: 保留每组开关与参数的相邻关系。
            Append("target_link_options", d.CpuFlags.Concat(GroupSdccLinkOptions(d.LinkOptions)));
        else
        {
            // Windows GCC 转发绝对中文路径到 ld 时可能改变编码；链接器从构建根目录解析相对脚本路径。
            text.AppendLine("file(RELATIVE_PATH _studiox_linker \"${CMAKE_BINARY_DIR}\" " + Quote(DevicePath(d.LinkerScript), generated: true) + ")");
            Append("target_link_options", d.CpuFlags.Concat(d.LinkOptions).Select(Checked).Append("-T${_studiox_linker}"), generated: true);
        }
        return text.ToString();

        void Append(string command, IEnumerable<string> values, bool generated = false)
        {
            var items = values.ToArray();
            if (items.Length == 0) return;
            text.AppendLine(command + "(studiox_device INTERFACE");
            foreach (var value in items) text.AppendLine("    " + Quote(value, generated));
            text.AppendLine(")\n");
        }
    }

    public static string RenderPlatform(BuildPlan plan)
    {
        if (plan.Device.Architecture == "mcs51")
            return ManagedMarker + "\n" + """
                # SDCC/CMake 原生生成 .ihx (Intel HEX)，不使用 ELF、objcopy 或 GCC 链接脚本。
                include_guard(GLOBAL)
                set(CMAKE_SYSTEM_NAME Generic)
                set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)

                function(studiox_configure_firmware target)
                    target_link_libraries(${target} PRIVATE studiox_device)
                    set_target_properties(${target} PROPERTIES SUFFIX .ihx)
                    add_custom_command(TARGET ${target} POST_BUILD
                        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:${target}>" "${CMAKE_CURRENT_BINARY_DIR}/${target}.hex"
                        BYPRODUCTS "${CMAKE_CURRENT_BINARY_DIR}/${target}.hex"
                        VERBATIM
                    )
                endfunction()
                """ + "\n";
        // POST_BUILD 必须在创建目标的目录注册，因此把函数定义在内部文件，由根目录调用。
        // 不能把 add_custom_command(TARGET firmware) 直接挪到 add_subdirectory(device) 的作用域。
        return ManagedMarker + "\n" + """
            # 自动生成：裸机环境和固件产物规则。通常无需修改。
            # 工具程序路径由 IDE 的工具集传入，工程中不记录开发机安装路径。
            include_guard(GLOBAL)
            set(CMAKE_SYSTEM_NAME Generic)
            set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)

            function(studiox_configure_firmware target)
                target_link_libraries(${target} PRIVATE studiox_device)
                set_target_properties(${target} PROPERTIES SUFFIX .elf)
                target_link_options(${target} PRIVATE
                    "-Wl,-Map=${target}.map"
                )
            """ + "\n    set_property(TARGET ${target} APPEND PROPERTY LINK_DEPENDS\n        " +
            Quote("${CMAKE_CURRENT_FUNCTION_LIST_DIR}/" + Checked(plan.Device.LinkerScript), generated: true) + "\n    )\n" + """
                set_property(TARGET ${target} APPEND PROPERTY ADDITIONAL_CLEAN_FILES
                    "${CMAKE_CURRENT_BINARY_DIR}/${target}.map"
                )
                add_custom_command(TARGET ${target} POST_BUILD
                    COMMAND "${CMAKE_OBJCOPY}" -O binary "$<TARGET_FILE_NAME:${target}>" "${target}.bin"
                    COMMAND "${CMAKE_OBJCOPY}" -O ihex "$<TARGET_FILE_NAME:${target}>" "${target}.hex"
                    BYPRODUCTS "${CMAKE_CURRENT_BINARY_DIR}/${target}.bin" "${CMAKE_CURRENT_BINARY_DIR}/${target}.hex"
                    VERBATIM
                )
            endfunction()
            """ + "\n";
    }

    private static string DevicePath(string path) => "${CMAKE_CURRENT_SOURCE_DIR}/" + Checked(path);
    private static IEnumerable<string> GroupSdccLinkOptions(IReadOnlyList<string> options)
    {
        for (var index = 0; index < options.Count; index++)
        {
            var option = options[index];
            if (option.StartsWith("--", StringComparison.Ordinal) && index + 1 < options.Count &&
                !options[index + 1].StartsWith("-", StringComparison.Ordinal))
                yield return "SHELL:" + option + " " + options[++index];
            else yield return option;
        }
    }
    private static string Checked(string value)
    {
        if (value.Any(c => c is '"' or ';' or '$' or '\n' or '\r' or '\\' or '\0')) throw new StudioXException("CMAKE_VALUE", "不支持的 CMake 参数字符。");
        return value;
    }
    private static string Quote(string value, bool generated = false)
    {
        _ = Checked(generated ? value.Replace("${CMAKE_CURRENT_SOURCE_DIR}", "").Replace("${CMAKE_CURRENT_FUNCTION_LIST_DIR}", "").Replace("${_studiox_linker}", "") : value);
        return '"' + value + '"';
    }
}
