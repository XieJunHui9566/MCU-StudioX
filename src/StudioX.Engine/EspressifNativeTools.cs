namespace StudioX.Engine;

using System.Text;
using StudioX.Foundation;

/// <summary>保留目标编译器文件名，在 CMake 检测与引导程序子工程中绑定同一组已锁定工具。</summary>
internal static class EspressifNativeTools
{
    internal const int Revision = 7;

    internal static async Task<string[]> PrepareAsync(string build, ResolvedToolset tools, EspressifProjectSettings sdk, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows() || sdk.Framework != "esp-idf")
        {
            return [];
        }
        var target = tools.ForEspressifTarget(sdk.Target);
        var xtensa = EspressifXtensaBinding.Create(tools, sdk);
        var hook = PathBoundary.Resolve(build, "studiox-native-tools.cmake");
        var script = new StringBuilder("# StudioX generated native paths; keep compiler basenames and SDK flags.\n");
        var sdkDirectory = tools.ResourceDirectory("idf").Replace('\\', '/');
        var nativeSdkDirectory = EspressifNativePath.For(tools.ResourceDirectory("idf")).Replace('\\', '/');
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
        var nativeHook = EspressifNativePath.For(hook).Replace('\\', '/');
        // 编译器身份检测早于 MAKE_RULES_OVERRIDE；先在 SDK 开发环境组件读完、语言启用前绑定原生路径。
        return new[] { "-D", "CMAKE_PROJECT_TOP_LEVEL_INCLUDES=" + nativeHook }
            .Concat(new[] { "C", "CXX", "ASM" }.SelectMany(language => new[] { "-D", "CMAKE_USER_MAKE_RULES_OVERRIDE_" + language + "=" + nativeHook })).ToArray();
    }
}
