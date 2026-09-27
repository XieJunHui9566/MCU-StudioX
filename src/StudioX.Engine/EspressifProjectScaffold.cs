namespace StudioX.Engine;

using StudioX.Foundation;
using StudioX.Packages;

/// <summary>生成原生 IDF 工程入口，SDK 源码和工具链不复制到每个工程。</summary>
internal static class EspressifProjectScaffold
{
    public static void Validate(ProjectManifest project)
    {
        if (project.Espressif is not { } settings)
        {
            if (project.ToolsetId is "espressif.idf" or "espressif.esp8266-rtos")
            {
                throw new StudioXException("PROJECT_ESPRESSIF_SETTINGS", "Espressif 工具集必须有明确的 SDK 与构建目标配置。");
            }
            return;
        }
        EspressifPackProfile.ValidateFramework(new EspressifDeviceDefinition(settings.Framework, settings.Target, settings.SdkVersion));
        if (settings.FormatVersion != 1 || project.Kind != ProjectKind.Pack || project.CubeMx is not null || project.Logic is not null ||
            project.ToolsetId != EspressifPackProfile.ToolsetId(settings.Framework) || project.ToolsetVersion != settings.SdkVersion ||
            project.CompilerId != EspressifPackProfile.CompilerId(settings.Framework))
        {
            throw new StudioXException("PROJECT_ESPRESSIF_SETTINGS", "Espressif 工程类型、SDK 与工具锁定信息不一致。");
        }
    }

    public static async Task WriteAsync(string directory, BuildPlan plan, CancellationToken token)
    {
        var settings = plan.Project.Espressif!;
        var template = plan.Device.Templates.Single(template => template.Id == plan.Project.TemplateId);
        if (template.EspressifExample is not null)
        {
            await EspressifExampleScaffold.WriteAsync(directory, plan, template, token);
            return;
        }
        var componentPath = Path.Combine(directory, "src", "CMakeLists.txt");
        if (File.Exists(componentPath))
        {
            throw new StudioXException("PROJECT_RESERVED_FILE", "器件包占用了 Espressif 应用组件的生成入口：src/CMakeLists.txt");
        }
        var targetLine = settings.Framework == "esp-idf"
            ? $"set(IDF_TARGET \"{settings.Target}\" CACHE STRING \"StudioX locked SDK target\")\n" : "";
        // 旧 SDK 需显式注册烧录/分区工具及 GCC 运行库引用的 pthread，不能仅保留应用依赖。
        var components = settings.Framework == "esp8266-rtos-sdk"
            ? "src esptool_py bootloader partition_table pthread" : "src";
        var root = "# 用户维护：添加组件时使用 EXTRA_COMPONENT_DIRS；SDK 与工具路径由 IDE 提供。\n" +
            "cmake_minimum_required(VERSION 3.16)\n\n" + targetLine +
            "set(EXTRA_COMPONENT_DIRS \"${CMAKE_CURRENT_LIST_DIR}/src\")\n" +
            $"set(COMPONENTS {components})\n" +
            "include($ENV{IDF_PATH}/tools/cmake/project.cmake)\n" +
            $"project({plan.Project.Name})\n";
        if (settings.Framework == "esp8266-rtos-sdk")
        {
            // SDK v3.4 的组件 CMake 强制符号少了 _var；在工程链接参数中引用真实实现，保留厂商源码。
            root += "\n# SDK v3.4 pthread 的实际条件变量强制符号；不改动共享 SDK。\n" +
                "target_link_options(${PROJECT_NAME}.elf PRIVATE \"-Wl,-u,pthread_include_pthread_cond_var_impl\")\n";
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "CMakeLists.txt"), root, token);
        var component = "# 用户维护：新增源文件或组件依赖时在这里声明，避免全目录递归收集。\n" +
            "idf_component_register(SRCS \"main.c\"\n" +
            "    INCLUDE_DIRS \".\" \"../include\"\n" +
            (settings.Framework == "esp-idf" ? "    PRIV_REQUIRES freertos esp_system)\n" : "    PRIV_REQUIRES freertos esp8266)\n");
        await File.WriteAllTextAsync(componentPath, component, token);
        var defaults = settings.Framework == "esp-idf" ? $"CONFIG_IDF_TARGET=\"{settings.Target}\"\n" : "";
        if (plan.Device.Id == "ESP32-WROOM-32")
        {
            // 只有已核验的具体模块容量可以成为默认配置；通用 SoC 目标由用户核对板级 Flash。
            defaults += "CONFIG_ESPTOOLPY_FLASHSIZE_4MB=y\n";
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "sdkconfig.defaults"), defaults, token);
    }
}
