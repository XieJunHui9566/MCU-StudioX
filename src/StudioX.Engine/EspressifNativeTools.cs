namespace StudioX.Engine;

using System.Text;
using StudioX.Foundation;

/// <summary>保留目标编译器文件名，在 CMake 检测与引导程序子工程中绑定同一组已锁定工具。</summary>
internal static class EspressifNativeTools
{
    internal const int Revision = 7;
    internal static int RevisionFor(EspressifProjectSettings sdk) => sdk.Framework == "esp8266-rtos-sdk" ? 8 : Revision;

    internal static async Task<string[]> PrepareAsync(string build, ResolvedToolset tools, EspressifProjectSettings sdk, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }
        var target = tools.ForEspressifTarget(sdk.Target);
        var xtensa = EspressifXtensaBinding.Create(tools, sdk);
        var hook = PathBoundary.Resolve(build, "studiox-native-tools.cmake");
        var script = new StringBuilder("# StudioX generated native paths; keep compiler basenames and SDK flags.\n");
        if (sdk.Framework == "esp8266-rtos-sdk")
        {
            // SDK 3.4 把路径写为未加引号的 set()，带空格的已安装 SDK 因而丢失全部组件。
            // 仅覆盖本次 CMake 的属性序列化，不修改 SDK、组件依赖和寄存器/代码生成规则。
            script.AppendLine("""
                function(_studiox_property_line file name value)
                    set(delimiter "==")
                    string(FIND "${value}" "]${delimiter}]" found)
                    while(NOT found EQUAL -1)
                        string(APPEND delimiter "=")
                        string(FIND "${value}" "]${delimiter}]" found)
                    endwhile()
                    file(APPEND "${file}" "set(${name} [${delimiter}[${value}]${delimiter}])\n")
                endfunction()
                function(__build_write_properties output_file)
                    file(WRITE "${output_file}" "")
                    idf_build_get_property(properties __BUILD_PROPERTIES)
                    foreach(property ${properties})
                        idf_build_get_property(value ${property})
                        _studiox_property_line("${output_file}" "${property}" "${value}")
                    endforeach()
                endfunction()
                function(__component_write_properties output_file)
                    file(WRITE "${output_file}" "")
                    idf_build_get_property(targets __COMPONENT_TARGETS)
                    foreach(target ${targets})
                        __component_get_property(properties ${target} __COMPONENT_PROPERTIES)
                        foreach(property ${properties})
                            __component_get_property(value ${target} ${property})
                            _studiox_property_line("${output_file}" "__component_${target}_${property}" "${value}")
                        endforeach()
                    endforeach()
                endfunction()
                """);
        }
        var sdkDirectory = tools.ResourceDirectory("idf").Replace('\\', '/');
        var nativeSdkDirectory = EspressifNativePath.For(tools.ResourceDirectory("idf")).Replace('\\', '/');
        if (sdk.Framework == "esp8266-rtos-sdk")
        {
            script.AppendLine($$$"""
                function(__component_get_property variable component property)
                    get_property(value TARGET ${component} PROPERTY ${property})
                    string(REPLACE "{{{sdkDirectory}}}/" "{{{nativeSdkDirectory}}}/" value "${value}")
                    set(${variable} "${value}" PARENT_SCOPE)
                endfunction()
                function(__ldgen_add_fragment_files fragment_files)
                    spaces2list(fragment_files)
                    set(fragments "")
                    foreach(fragment ${fragment_files})
                        get_filename_component(path "${fragment}" ABSOLUTE)
                        string(REPLACE "{{{sdkDirectory}}}/" "{{{nativeSdkDirectory}}}/" path "${path}")
                        list(APPEND fragments "${path}")
                    endforeach()
                    idf_build_set_property(__LDGEN_FRAGMENT_FILES "${fragments}" APPEND)
                endfunction()
                function(__ldgen_process_template template output)
                    idf_build_get_property(idf_path IDF_PATH)
                    idf_build_get_property(build_dir BUILD_DIR)
                    idf_build_get_property(libraries __LDGEN_LIBRARIES GENERATOR_EXPRESSION)
                    file(GENERATE OUTPUT "${build_dir}/ldgen_libraries.in" CONTENT "$<JOIN:${libraries},\n>")
                    file(GENERATE OUTPUT "${build_dir}/ldgen_libraries" INPUT "${build_dir}/ldgen_libraries.in")
                    set_property(DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}" APPEND PROPERTY ADDITIONAL_MAKE_CLEAN_FILES
                        "${build_dir}/ldgen_libraries.in" "${build_dir}/ldgen_libraries")
                    idf_build_get_property(fragments __LDGEN_FRAGMENT_FILES GENERATOR_EXPRESSION)
                    idf_build_get_property(depends __LDGEN_DEPENDS GENERATOR_EXPRESSION)
                    idf_build_get_property(sdkconfig SDKCONFIG)
                    idf_build_get_property(kconfig __ROOT_KCONFIG)
                    idf_build_get_property(config_env CONFIG_ENV_PATH)
                    idf_build_get_property(python PYTHON)
                    add_custom_command(OUTPUT "${output}"
                        COMMAND "${python}" "${idf_path}/tools/ldgen/ldgen.py"
                            --config "${sdkconfig}" --fragments "${fragments}"
                            --input "${template}" --output "${output}" --kconfig "${kconfig}"
                            --env-file "${config_env}" --libraries-file "${build_dir}/ldgen_libraries"
                            --objdump "${CMAKE_OBJDUMP}"
                        DEPENDS "${template}" ${fragments} ${depends} "${sdkconfig}"
                        VERBATIM COMMAND_EXPAND_LISTS)
                    get_filename_component(name "${output}" NAME)
                    add_custom_target(__ldgen_output_${name} DEPENDS "${output}")
                    add_dependencies(__idf_build_target __ldgen_output_${name})
                    idf_build_set_property(__LINK_DEPENDS "${output}" APPEND)
                endfunction()
                include(ExternalProject)
                if(NOT COMMAND _externalproject_add)
                    function(_studiox_external_argument variable value)
                        set(delimiter "==")
                        string(FIND "${value}" "]${delimiter}]" found)
                        while(NOT found EQUAL -1)
                            string(APPEND delimiter "=")
                            string(FIND "${value}" "]${delimiter}]" found)
                        endwhile()
                        set(${variable} "${${variable}} [${delimiter}[${value}]${delimiter}]" PARENT_SCOPE)
                    endfunction()
                    function(externalproject_add name)
                        set(invocation "_externalproject_add(")
                        math(EXPR last "${ARGC} - 1")
                        foreach(index RANGE 0 ${last})
                            set(argument "${ARGV${index}}")
                            _studiox_external_argument(invocation "${argument}")
                            if(name STREQUAL "bootloader" AND argument STREQUAL "CMAKE_ARGS")
                                foreach(flag CMAKE_PROJECT_TOP_LEVEL_INCLUDES CMAKE_USER_MAKE_RULES_OVERRIDE_C CMAKE_USER_MAKE_RULES_OVERRIDE_CXX CMAKE_USER_MAKE_RULES_OVERRIDE_ASM)
                                    _studiox_external_argument(invocation "-D${flag}=${CMAKE_CURRENT_FUNCTION_LIST_FILE}")
                                endforeach()
                                _studiox_external_argument(invocation "-DCMAKE_MAKE_PROGRAM=${CMAKE_MAKE_PROGRAM}")
                            endif()
                        endforeach()
                        # 每个原始实参都使用独立 bracket literal，包括 INSTALL_COMMAND 的空字符串；不二次展开内容。
                        string(APPEND invocation ")")
                        cmake_language(EVAL CODE "${invocation}")
                    endfunction()
                endif()
                """);
            // 旧 Kconfig 用空格分隔文件列表；组件发现可能已还原 SDK 的长路径，必须在生成列表前换回兼容路径。
            script.AppendLine($$$"""
                function(__kconfig_component_init component_target)
                    __component_get_property(directory ${component_target} COMPONENT_DIR)
                    file(GLOB kconfig "${directory}/Kconfig")
                    string(REPLACE "{{{sdkDirectory}}}/" "{{{nativeSdkDirectory}}}/" kconfig "${kconfig}")
                    __component_set_property(${component_target} KCONFIG "${kconfig}")
                    file(GLOB kconfig "${directory}/Kconfig.projbuild")
                    string(REPLACE "{{{sdkDirectory}}}/" "{{{nativeSdkDirectory}}}/" kconfig "${kconfig}")
                    __component_set_property(${component_target} KCONFIG_PROJBUILD "${kconfig}")
                endfunction()
                if(COMMAND idf_build_get_property AND TARGET __idf_build_target)
                    idf_build_get_property(_studiox_components __COMPONENT_TARGETS)
                    foreach(_studiox_component ${_studiox_components})
                        __kconfig_component_init(${_studiox_component})
                    endforeach()
                    function(_studiox_normalize_sdk_links)
                        idf_build_get_property(components __BUILD_COMPONENT_TARGETS)
                        set(libraries "")
                        foreach(component ${components})
                            __component_get_property(library ${component} COMPONENT_LIB)
                            list(APPEND libraries ${library})
                        endforeach()
                        if(TARGET "${CMAKE_PROJECT_NAME}.elf")
                            list(APPEND libraries "${CMAKE_PROJECT_NAME}.elf")
                        endif()
                        foreach(library ${libraries})
                            foreach(property LINK_LIBRARIES INTERFACE_LINK_LIBRARIES LINK_OPTIONS INTERFACE_LINK_OPTIONS LINK_DIRECTORIES INTERFACE_LINK_DIRECTORIES)
                                get_target_property(value ${library} ${property})
                                if(value)
                                    string(REPLACE "{{{sdkDirectory}}}/" "{{{nativeSdkDirectory}}}/" value "${value}")
                                    set_property(TARGET ${library} PROPERTY ${property} "${value}")
                                endif()
                            endforeach()
                        endforeach()
                    endfunction()
                    cmake_language(DEFER CALL _studiox_normalize_sdk_links)
                endif()
                """);
        }
        // IDF 的链接片段收集会按空格拆分绝对 SDK 路径，蓝牙控制器片段因此成为不存在的依赖。
        // 只在生成的 hook 中绑定已锁定 SDK 的原生目录，不改写共享 SDK 或用户片段。
        script.Append("if(COMMAND __ldgen_add_fragment_files AND NOT COMMAND ___ldgen_add_fragment_files)\n" +
            "  function(__ldgen_add_fragment_files _studiox_native_fragments)\n" +
            $"    string(REPLACE \"{sdkDirectory}/\" \"{nativeSdkDirectory}/\" _studiox_native_fragments \"${{_studiox_native_fragments}}\")\n" +
            "    ___ldgen_add_fragment_files(\"${_studiox_native_fragments}\")\n" +
            "  endfunction()\n" +
            "endif()\n");
        foreach (var (variable, role) in new (string, string)[]
        {
            ("CMAKE_C_COMPILER", "gcc"), ("CMAKE_CXX_COMPILER", "gxx"), ("CMAKE_ASM_COMPILER", "gcc"),
            ("CMAKE_AR", "ar"), ("CMAKE_RANLIB", "ranlib"), ("CMAKE_OBJCOPY", "objcopy"), ("CMAKE_OBJDUMP", "objdump")
        })
        {
            if (!target.Manifest.Executables.ContainsKey(role))
            {
                continue;
            }
            var executable = xtensa is null ? target.Tool(role) : role switch
            {
                "gcc" => xtensa.Gcc,
                "gxx" => xtensa.Gxx,
                _ => target.Tool(role)
            };
            var path = EspressifNativePath.ForExecutable(executable).Replace('\\', '/');
            // 两种绑定都写，避免 CMP0126 政策及已有缓存重新恢复带空格的规范路径。
            script.AppendLine($"set({variable} \"{path}\")");
            script.AppendLine($"set({variable} \"{path}\" CACHE FILEPATH \"StudioX locked native tool\" FORCE)");
        }
        if (xtensa is not null)
        {
            // 保留裸 SO 名作为 GCC 多库选择键；绝对配置文件由本次进程环境绑定，不能把 -mdynconfig 改成绝对路径丢失多库。
            foreach (var language in new[] { "C", "CXX", "ASM" })
            {
                script.AppendLine($"if(NOT \" ${{CMAKE_{language}_FLAGS}} \" MATCHES \" -mdynconfig={xtensa.ConfigName.Replace(".", "[.]")} \")");
                script.AppendLine($"  string(APPEND CMAKE_{language}_FLAGS \" -mdynconfig={xtensa.ConfigName}\")");
                script.AppendLine($"  set(CMAKE_{language}_FLAGS \"${{CMAKE_{language}_FLAGS}}\" CACHE STRING \"SDK and locked Xtensa config\" FORCE)");
                script.AppendLine("endif()");
            }
        }
        // IDF 的旧 Git 版本探测在新建但尚无提交的仓库中会读取不存在的 head-ref。
        // 返回 SDK 既有的“无提交版本”状态，让 IDF 自己保留用户版本来源及默认值；不设置 PROJECT_VER。
        script.Append("if(COMMAND get_git_head_revision AND NOT COMMAND _get_git_head_revision)\n" +
            "  function(get_git_head_revision _refspecvar _hashvar _repo_dir)\n" +
            "    if(EXISTS \"${_repo_dir}/.git\" AND GIT_EXECUTABLE)\n" +
            "      execute_process(COMMAND \"${GIT_EXECUTABLE}\" symbolic-ref --quiet HEAD WORKING_DIRECTORY \"${_repo_dir}\"\n" +
            "        RESULT_VARIABLE _studiox_native_branch_result OUTPUT_VARIABLE _studiox_native_branch OUTPUT_STRIP_TRAILING_WHITESPACE)\n" +
            "      if(_studiox_native_branch_result EQUAL 0)\n" +
            "        execute_process(COMMAND \"${GIT_EXECUTABLE}\" show-ref --verify --quiet \"${_studiox_native_branch}\"\n" +
            "          WORKING_DIRECTORY \"${_repo_dir}\" RESULT_VARIABLE _studiox_native_ref_result)\n" +
            "        if(_studiox_native_ref_result EQUAL 1)\n" +
            "          set(${_refspecvar} \"${_studiox_native_branch}\" PARENT_SCOPE)\n" +
            "          set(${_hashvar} \"HEAD-HASH-NOTFOUND\" PARENT_SCOPE)\n" +
            "          message(STATUS \"StudioX: Git repository has no commits; skipping revision lookup.\")\n" +
            "          return()\n" +
            "        endif()\n" +
            "      endif()\n" +
            "    endif()\n" +
            "    _get_git_head_revision(\"${_refspecvar}\" \"${_hashvar}\" \"${_repo_dir}\")\n" +
            "    set(${_refspecvar} \"${${_refspecvar}}\" PARENT_SCOPE)\n" +
            "    set(${_hashvar} \"${${_hashvar}}\" PARENT_SCOPE)\n" +
            "  endfunction()\n" +
            "endif()\n");
        // IDF 只通过 EXTRA_CMAKE_ARGS 向 bootloader 传递额外参数；主工程成功不能掩盖子工程仍走短文件名。
        script.Append("if(COMMAND idf_build_set_property AND TARGET __idf_build_target AND NOT BOOTLOADER_BUILD)\n" +
            "  idf_build_get_property(_studiox_native_extra EXTRA_CMAKE_ARGS)\n" +
            "  set(_studiox_native_early \"-DCMAKE_PROJECT_TOP_LEVEL_INCLUDES=${CMAKE_CURRENT_LIST_FILE}\")\n" +
            "  if(NOT _studiox_native_early IN_LIST _studiox_native_extra)\n" +
            "    idf_build_set_property(EXTRA_CMAKE_ARGS \"${_studiox_native_early}\" APPEND)\n" +
            "  endif()\n" +
            "  foreach(_studiox_native_language C CXX ASM)\n" +
            "    set(_studiox_native_override \"-DCMAKE_USER_MAKE_RULES_OVERRIDE_${_studiox_native_language}=${CMAKE_CURRENT_LIST_FILE}\")\n" +
            "    if(NOT _studiox_native_override IN_LIST _studiox_native_extra)\n" +
            "      idf_build_set_property(EXTRA_CMAKE_ARGS \"${_studiox_native_override}\" APPEND)\n" +
            "    endif()\n" +
            "  endforeach()\n" +
            "endif()\n");
        await File.WriteAllTextAsync(hook, script.ToString(), token);
        var nativeHook = EspressifNativePath.ForProject(hook).Replace('\\', '/');
        // 编译器身份检测早于 MAKE_RULES_OVERRIDE；先在 SDK 开发环境组件读完、语言启用前绑定原生路径。
        return new[] { "-D", "CMAKE_PROJECT_TOP_LEVEL_INCLUDES=" + nativeHook }
            .Concat(new[] { "C", "CXX", "ASM" }.SelectMany(language => new[] { "-D", "CMAKE_USER_MAKE_RULES_OVERRIDE_" + language + "=" + nativeHook })).ToArray();
    }
}
