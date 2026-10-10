namespace StudioX.KeilImporter;

using System.Text;
using System.Text.Json;

/// <summary>按公开格式 1 补充器件构建支持；不调用新建模板，不生成应用入口。</summary>
public static class PortBuildSupport
{
    private const string Marker = "# StudioX managed device support (layout 2)";

    public static async Task WriteAsync(ImportPreview preview, string project,
        Func<CopyInput, string, CancellationToken, Task> copy, CancellationToken token)
    {
        var request = preview.Request;
        var device = request.Device.Device;
        var template = device.GetProperty("templates").EnumerateArray().Single(item => item.GetProperty("id").GetString() == request.TemplateId);
        var payload = Path.Combine(request.Device.PackDirectory, "payload");
        var index = await DeviceCatalog.ReadJsonAsync(Path.Combine(request.Device.PackDirectory, "files.sha256.json"), token);
        var resources = new HashSet<string>(preview.PackSources, StringComparer.Ordinal);
        resources.Add("manifest.json");
        resources.Add(device.GetProperty("linkerScript").GetString()!);
        if (device.TryGetProperty("openOcd", out var openOcd) && openOcd.ValueKind == JsonValueKind.Object)
        {
            resources.Add(openOcd.GetProperty("targetScript").GetString()!);
            foreach (var entry in index.EnumerateObject().Where(entry => entry.Name.StartsWith("debug/", StringComparison.Ordinal))) { resources.Add(entry.Name); }
        }
        if (preview.PackSources.Any(source => Path.GetFileName(source).StartsWith("system_stm32", StringComparison.OrdinalIgnoreCase)))
        {
            var includes = ImportPlanner.Strings(device, "includeDirectories").Concat(template.TryGetProperty("build", out var build)
                ? ImportPlanner.Strings(build, "includeDirectories") : []);
            foreach (var include in includes)
            {
                foreach (var entry in index.EnumerateObject().Where(entry => entry.Name.StartsWith(include.TrimEnd('/') + "/", StringComparison.Ordinal))) { resources.Add(entry.Name); }
            }
        }
        foreach (var relative in resources.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var source = ImportPaths.PackPath(payload, relative);
            var destination = ImportPaths.PackPath(project, "device/" + relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            var expected = index.GetProperty(relative).GetString()!;
            await copy(new(source, relative, new FileInfo(source).Length, expected), destination, token);
        }
        await File.WriteAllTextAsync(Path.Combine(project, "device", "CMakeLists.txt"), RenderDevice(device, template), new UTF8Encoding(false), token);
        await File.WriteAllTextAsync(Path.Combine(project, "device", "platform.cmake"), RenderPlatform(device), new UTF8Encoding(false), token);
        Directory.CreateDirectory(Path.Combine(project, ".studiox"));
        var components = new List<JsonElement>();
        foreach (var layer in new[] { device, template })
        {
            if (!layer.TryGetProperty("developmentComponents", out var needs) || needs.ValueKind != JsonValueKind.Array) { continue; }
            foreach (var need in needs.EnumerateArray())
            {
                var previous = components.FirstOrDefault(item => item.GetProperty("id").GetString() == need.GetProperty("id").GetString());
                if (previous.ValueKind != JsonValueKind.Undefined &&
                    (previous.GetProperty("version").GetString() != need.GetProperty("version").GetString() ||
                    previous.GetProperty("compilerId").GetString() != need.GetProperty("compilerId").GetString() ||
                    ComponentHost(previous) != ComponentHost(need)))
                {
                    throw new InvalidOperationException("器件与构建配置的开发环境组件身份冲突。");
                }
                if (previous.ValueKind == JsonValueKind.Undefined) { components.Add(need.Clone()); }
            }
        }
        if (!components.Any(item => item.GetProperty("id").GetString() == device.GetProperty("toolsetId").GetString()))
        {
            components.Add(JsonSerializer.SerializeToElement(new { id = device.GetProperty("toolsetId").GetString(), version = device.GetProperty("toolsetVersion").GetString(),
                compilerId = device.GetProperty("compilerId").GetString(), host = "win-x64", purpose = "构建" }));
        }
        var manifest = new { formatVersion = 1, name = request.ProjectName, packId = request.Device.PackId, packVersion = request.Device.PackVersion,
            packContentHash = request.Device.ContentHash, deviceId = request.Device.Id, templateId = request.TemplateId,
            toolsetId = device.GetProperty("toolsetId").GetString(), toolsetVersion = device.GetProperty("toolsetVersion").GetString(),
            compilerId = device.GetProperty("compilerId").GetString(), kind = "Pack", developmentComponents = components };
        await File.WriteAllTextAsync(Path.Combine(project, ".studiox", "project.json"), JsonSerializer.Serialize(manifest, DeviceCatalog.Json), new UTF8Encoding(false), token);
    }

    private static string ComponentHost(JsonElement component) => component.TryGetProperty("host", out var host) ? host.GetString()! : "win-x64";

    private static string RenderDevice(JsonElement device, JsonElement template)
    {
        var text = new StringBuilder(Marker + "\n# 仅提供器件 CPU/ABI/链接选项；源码、包含目录和宏来自原 Keil Target。\nadd_library(studiox_device INTERFACE)\n");
        var cpu = ImportPlanner.Strings(device, "cpuFlags");
        var build = template.TryGetProperty("build", out var value) ? value : default;
        Append("target_compile_options", cpu.Concat(ImportPlanner.Strings(device, "compileOptions"))
            .Concat(build.ValueKind == JsonValueKind.Object ? ImportPlanner.Strings(build, "compileOptions") : []).Distinct(StringComparer.Ordinal));
        var linker = device.GetProperty("linkerScript").GetString()!;
        _ = ImportPaths.CMakeValue(linker);
        text.AppendLine("file(RELATIVE_PATH _studiox_linker \"${CMAKE_BINARY_DIR}\" \"${CMAKE_CURRENT_SOURCE_DIR}/" + linker + "\")");
        Append("target_link_options", cpu.Concat(ImportPlanner.Strings(device, "linkOptions"))
            .Concat(build.ValueKind == JsonValueKind.Object ? ImportPlanner.Strings(build, "linkOptions") : []).Distinct(StringComparer.Ordinal));
        text.AppendLine("target_link_options(studiox_device INTERFACE \"-T${_studiox_linker}\")");
        return text.ToString();
        void Append(string command, IEnumerable<string> values)
        {
            text.AppendLine(command + "(studiox_device INTERFACE");
            foreach (var entry in values) { text.AppendLine("    " + ImportPaths.CMakeValue(entry)); }
            text.AppendLine(")");
        }
    }

    private static string RenderPlatform(JsonElement device)
    {
        var linker = device.GetProperty("linkerScript").GetString()!;
        _ = ImportPaths.CMakeValue(linker);
        return Marker + "\n" + """
            # 工具程序由 IDE 注入，工程内不保存本机工具路径。
            include_guard(GLOBAL)
            set(CMAKE_SYSTEM_NAME Generic)
            set(CMAKE_TRY_COMPILE_TARGET_TYPE STATIC_LIBRARY)
            function(studiox_configure_firmware target)
                target_link_libraries(${target} PRIVATE studiox_device)
                set_target_properties(${target} PROPERTIES SUFFIX .elf)
                target_link_options(${target} PRIVATE "-Wl,-Map=${target}.map")
            """ + "\n    set_property(TARGET ${target} APPEND PROPERTY LINK_DEPENDS \"${CMAKE_CURRENT_FUNCTION_LIST_DIR}/" + linker + "\")\n" + """
                set_property(TARGET ${target} APPEND PROPERTY ADDITIONAL_CLEAN_FILES "${CMAKE_CURRENT_BINARY_DIR}/${target}.map")
                add_custom_command(TARGET ${target} POST_BUILD
                    COMMAND "${CMAKE_OBJCOPY}" -O binary "$<TARGET_FILE_NAME:${target}>" "${target}.bin"
                    COMMAND "${CMAKE_OBJCOPY}" -O ihex "$<TARGET_FILE_NAME:${target}>" "${target}.hex"
                    BYPRODUCTS "${CMAKE_CURRENT_BINARY_DIR}/${target}.bin" "${CMAKE_CURRENT_BINARY_DIR}/${target}.hex"
                    VERBATIM
                )
            endfunction()
            """ + "\n";
    }
}
