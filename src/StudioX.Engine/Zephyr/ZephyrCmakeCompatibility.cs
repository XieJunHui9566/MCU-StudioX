namespace StudioX.Engine.Zephyr;

/// <summary>通过 CMake 的工程钩子统一 Windows 短路径和真实路径，不修改锁定 SDK。</summary>
internal static class ZephyrCmakeCompatibility
{
    internal static async Task<string> WriteAsync(string cache, CancellationToken token)
    {
        var path = Path.Combine(cache, "studiox-paths.cmake");
        await File.WriteAllTextAsync(path, """
            # SPDX-License-Identifier: Apache-2.0
            # Derived from Zephyr 4.4.2 cmake/modules/extensions.cmake.
            # Windows 会在子目录中恢复长路径；计算库名之前先统一两端的真实路径。
            macro(zephyr_library_get_current_dir_lib_name base lib_name)
              file(REAL_PATH "${base}" _studiox_library_base)
              file(REAL_PATH "${CMAKE_CURRENT_LIST_FILE}" _studiox_library_file)
              file(RELATIVE_PATH name "${_studiox_library_base}" "${_studiox_library_file}")
              get_filename_component(name "${name}" DIRECTORY)
              string(REGEX REPLACE "/" "__" name "${name}")
              string(REGEX REPLACE ":" "__" name "${name}")
              string(REGEX REPLACE "~" "-" name "${name}")
              set(${lib_name} "${name}")
            endmacro()
            """ + "\n", token);
        return ZephyrNativePath.Resolve(path);
    }
}
