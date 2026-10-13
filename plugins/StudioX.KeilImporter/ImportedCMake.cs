namespace StudioX.KeilImporter;

using System.Text;

public static class ImportedCMake
{
    public static string Render(ImportRequest request, KeilTarget target, string[] sources, string[] includes,
        string[] packSources, string[] additionalDefines)
    {
        var result = new StringBuilder("""
            cmake_minimum_required(VERSION 3.24)

            # 迁移后的用户配置；芯片参数和 GNU 链接脚本仍来自已校验的器件包。
            include(device/platform.cmake)
            project(firmware LANGUAGES C CXX ASM)
            set(CMAKE_C_STANDARD 11)
            set(CMAKE_EXPORT_COMPILE_COMMANDS ON)
            add_executable(firmware)
            add_subdirectory(device)

            # 使用原 Target 的厂商源码和头文件，避免混入另一版本 HAL/SPL。
            # 器件支持只提供 CPU/ABI/链接参数；应用和厂商库来自原 Keil Target。
            studiox_configure_firmware(firmware)

            """ + "\n");
        Append("target_sources", sources.Concat(packSources.Select(source => "device/" + source)));
        Append("target_include_directories", includes);
        // 器件包中的晶振/向量表默认值属于模板应用，不能覆盖原工程的配置头文件。
        Append("target_compile_definitions", target.Defines.Concat(additionalDefines).Distinct(StringComparer.Ordinal));
        if (packSources.Any(source => Path.GetFileName(source).StartsWith("system_stm32", StringComparison.OrdinalIgnoreCase)))
        {
            var template = request.Device.Device.GetProperty("templates").EnumerateArray().Single(template => template.GetProperty("id").GetString() == request.TemplateId);
            var templateIncludes = template.TryGetProperty("build", out var build) ? ImportPlanner.Strings(build, "includeDirectories") : [];
            Append("target_include_directories", ImportPlanner.Strings(request.Device.Device, "includeDirectories").Concat(templateIncludes).Distinct(StringComparer.Ordinal).Select(include => "device/" + include));
        }
        return result.ToString();

        void Append(string command, IEnumerable<string> values)
        {
            var entries = values.ToArray();
            if (entries.Length == 0)
            {
                return;
            }
            result.AppendLine(command + "(firmware PRIVATE");
            foreach (var value in entries)
            {
                result.AppendLine("    " + ImportPaths.CMakeValue(value));
            }
            result.AppendLine(")\n");
        }
    }
}
