using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class ProjectNameChecks
{
    internal static async Task VerifyExistingAsync(string runtime, string root, string output)
    {
        Directory.CreateDirectory(output);
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builder = new BuildService(catalog);
        var report = await builder.BuildAsync(root, output: new ConsoleProgress());
        if (!report.Success)
        {
            throw new InvalidOperationException(report.Log);
        }
        var preview = await new EspressifFlashService(catalog).PreviewAsync(root, new(Port: "COM1"));
        using var database = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".build/compile_commands.json")));
        if (!database.RootElement.EnumerateArray().Any(item => item.GetProperty("file").GetString()!.Replace('\\', '/').StartsWith(root.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("编译数据库没有指回中文源码目录。");
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            passed = true,
            hardware = false,
            report.Success,
            preview.Layout.LayoutSha256
        });
        Console.WriteLine("PASS cached build, source mapping and download preview");
    }

    internal static async Task RunBackendAsync(string runtime, string packs, string output, string backend)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("验收目录必须尚不存在。");
        }
        var name = "中文工程_" + backend;
        var root = Path.Combine(output, backend is "esp-idf" or "esp8266" ? "中文父目录" : "中文父目录 with spaces", name);
        var installed = await new PackRepository(packs).ListCatalogAsync();
        ProjectManifest project;
        if (backend.StartsWith("arduino", StringComparison.Ordinal))
        {
            project = await new StudioX.Engine.Arduino.ArduinoProjectService().CreateAsync(name,
                backend == "arduino-avr" ? "uno" : "esp32", root);
        }
        else
        {
            var id = backend switch
            {
                "arm" or "zephyr" => "studiox.stm32f407",
                "riscv" => "wch.ch32v307",
                "pic" => "microchip.pic16f877a",
                "mx251" => "stc.ai8051u-mx251",
                "mx251-stc32" => "stc.stc32g-mx251",
                "esp-idf" => "espressif.esp32-wroom-32",
                "esp8266" => "espressif.esp8266",
                "pico" => "raspberrypi.rp2350",
                _ => throw new InvalidOperationException("未知验收后端：" + backend)
            };
            var selected = installed.Last(item => item.Manifest.Id == id && (backend != "esp-idf" || item.Manifest.Devices[0].ToolsetVersion == "5.5.4"));
            var part = selected.Manifest.Devices.First();
            project = backend == "zephyr"
                ? await new ZephyrProjectService().CreateFromPackAsync(selected, part.Id, name, root)
                : await new ProjectService().CreateAsync(selected, part.Id, part.Templates[0].Id, name, root);
        }
        if ((await ProjectService.ReadAsync(root)).Name != name)
        {
            throw new InvalidOperationException("中文名称未保留。");
        }
        var catalog = new ToolsetCatalog(Path.Combine(runtime, "toolsets"));
        var builder = new BuildService(catalog);
        var report = await builder.BuildAsync(root, output: new ConsoleProgress());
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            backend,
            name,
            project.DeviceId,
            project.TemplateId,
            report.Success,
            report.Artifacts,
            report.LogPath
        });
        if (!report.Success)
        {
            throw new InvalidOperationException(report.Log);
        }
        if (project.Kind == ProjectKind.Arduino)
        {
            _ = await builder.Arduino.PreviewAsync(root);
        }
        if (project.Espressif is not null)
        {
            _ = await new EspressifFlashService(catalog).PreviewAsync(root, new(Port: "COM1"));
        }
        Console.WriteLine("PASS " + backend + " " + name);
    }

    private sealed class ConsoleProgress : IProgress<string>
    {
        public void Report(string value) => Console.Write(value);
    }

    internal static async Task RunAsync(string runtime, string packs, string output)
    {
        if (Directory.Exists(output))
        {
            throw new InvalidOperationException("验收目录必须尚不存在。");
        }
        Directory.CreateDirectory(output);
        foreach (var invalid in new[] { "", "../escape", "a/b", "a\\b", "C:escape", "CON", "nul.txt", "CON .txt", "LPT1 .txt", "COM¹", "trailing.", "leading ", " name", "a\nb", "a;message(FATAL_ERROR)", "a${test}", new string('中', 81) })
        {
            try
            {
                ProjectName.Validate(invalid);
                throw new InvalidOperationException("接受了非法工程名称：" + invalid);
            }
            catch (StudioXException)
            {
            }
        }
        foreach (var valid in new[] { "四梯开发板", "四梯 开发板_2", "my_firmware", "test-1.2", "8051开发板" })
        {
            ProjectName.Validate(valid);
        }
        try
        {
            PackValidator.Token("四梯开发板");
            throw new InvalidOperationException("组件标识校验被放宽。");
        }
        catch (StudioXException)
        {
        }

        var installed = await new PackRepository(packs).ListCatalogAsync();
        // 各创建入口都必须能重开中文名称；不把此处的文件检查表述为对应工具链或实板验收。
        foreach (var id in new[] { "microchip.pic16f877a", "espressif.esp32-wroom-32" })
        {
            var selected = installed.LastOrDefault(item => item.Manifest.Id == id);
            if (selected is null)
            {
                throw new InvalidOperationException("缺少创建入口验收包：" + id);
            }
            var part = selected.Manifest.Devices.First();
            var target = Path.Combine(output, "其他创建入口", id);
            var created = await new ProjectService().CreateAsync(selected, part.Id, part.Templates[0].Id, "中文工程", target);
            if ((await ProjectService.ReadAsync(target)).Name != created.Name)
            {
                throw new InvalidOperationException("中文工程无法重开：" + id);
            }
            if (created.Espressif is not null && (await File.ReadAllTextAsync(Path.Combine(target, "CMakeLists.txt"))).Contains("project(中文", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("显示名称泄漏至原生 SDK 标识。");
            }
            Console.WriteLine("PASS create/reopen " + id);
        }
        var pack = installed.Single(item => item.Manifest.Id == "stc.stc8" && item.Manifest.Version == "0.1.9");
        var device = pack.Manifest.Devices.Single(item => item.Id == "IAP15L2K61S2");
        var builder = new BuildService(new ToolsetCatalog(Path.Combine(runtime, "toolsets")));
        var rows = new List<object>();
        foreach (var name in new[] { "四梯开发板", "四梯 开发板_2" })
        {
            var root = Path.Combine(output, "中文父目录 with spaces", name);
            var project = await new ProjectService().CreateAsync(pack, device.Id, device.Templates[0].Id, name, root);
            if ((await ProjectService.ReadAsync(root)).Name != name)
            {
                throw new InvalidOperationException("重开后中文名称不一致。");
            }
            var report = await builder.BuildAsync(root);
            if (!report.Success)
            {
                throw new InvalidOperationException(report.Log);
            }
            var firmware = Path.Combine(root, ".build/firmware.hex");
            if (!File.Exists(firmware))
            {
                throw new InvalidOperationException("缺少有效固件。");
            }
            rows.Add(new
            {
                name = project.Name,
                report.Success,
                report.LogPath,
                firmware
            });
            Console.WriteLine("PASS " + name);
        }
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), rows);
    }
}
