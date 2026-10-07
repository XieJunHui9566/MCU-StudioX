using StudioX.Application.CodeIntelligence;
using StudioX.Engine;

internal static class LanguageChecks
{
    public static async Task<int> KeilAsync(string runtime, string root)
    {
        foreach (var name in new[] { "real-f103-spl", "real-f407-hal" })
        {
            var directory = Path.GetFullPath(Path.Combine(root, name));
            var project = await ProjectService.ReadAsync(directory);
            var path = project.EntryFile!;
            var text = await File.ReadAllTextAsync(Path.Combine(directory, path));
            await using var service = new CodeIntelligenceService(Path.GetFullPath(runtime), Path.Combine(root, "keil-language-cache"));
            await service.StartAsync(directory);
            await service.SynchronizeDiagnosticsAsync(path, text, [new(path, text)]);
            var batch = service.GetDiagnostics().FirstOrDefault(b => b.Path == path && b.Text == text);
            for (var i = 0; i < 150 && batch is null; i++)
            {
                await Task.Delay(50);
                batch = service.GetDiagnostics().FirstOrDefault(b => b.Path == path && b.Text == text);
            }
            if (batch is null || batch.Items.Any(d => d.Severity == 1))
            {
                await File.WriteAllLinesAsync(Path.Combine(directory, "language.log"), service.DrainLog());
                throw new Exception(name + ": " + System.Text.Json.JsonSerializer.Serialize(batch?.Items));
            }
            var symbol = name == "real-f103-spl" ? "OLED_Init" : "HAL_Init";
            var offset = text.IndexOf(symbol, StringComparison.Ordinal);
            if (offset < 0)
            {
                throw new Exception("Fixture lacks symbol " + symbol);
            }
            var locations = await service.NavigateAsync(path, text, offset + 2, true);
            if (locations.Count == 0)
            {
                throw new Exception("Original Keil header jump failed: " + symbol);
            }
            Console.WriteLine("PASS actual Keil: " + name + ", clean original main diagnostics and header jump " + symbol);
        }
        return 0;
    }

    public static async Task<int> RunAsync(string runtime, string root)
    {
        var count = 0;
        foreach (var directory in Directory.GetDirectories(Path.GetFullPath(root)).Where(d => File.Exists(Path.Combine(d, ".studiox/project.json"))))
        {
            var project = await ProjectService.ReadAsync(directory);
            var hal = project.TemplateId.StartsWith("hal", StringComparison.Ordinal);
            var ll = project.TemplateId.StartsWith("ll", StringComparison.Ordinal);
            var rtos = project.TemplateId.Contains("freertos", StringComparison.Ordinal);
            await using var service = new CodeIntelligenceService(Path.GetFullPath(runtime), Path.Combine(root, "language-cache"));
            await service.StartAsync(directory);
            var prefix = "#include \"board.h\"\n" + (ll ? "#include \"" + (project.DeviceId.StartsWith("STM32F1", StringComparison.Ordinal) ? "stm32f1xx" : "stm32f4xx") + "_ll_gpio.h\"\n" : "") + (rtos ? "#include \"FreeRTOS.h\"\n#include \"task.h\"\n" : "");
            foreach (var (partial, expected) in new[] { (hal ? "HAL_GPIO_Wri" : ll ? "LL_GPIO_SetOutputP" : "GPIO_SetB", hal ? "HAL_GPIO_WritePin" : ll ? "LL_GPIO_SetOutputPin" : "GPIO_SetBits"), ("RCC->", "CFGR") }.Concat(rtos ? new[] { ("vTaskDel", "vTaskDelay") } : []))
            {
                var source = prefix + "void example(void) { " + partial + "\n}";
                var completions = await service.CompleteAsync("src/main.c", source, source.LastIndexOf(partial, StringComparison.Ordinal) + partial.Length);
                if (!completions.Any(c => c.InsertText.Contains(expected, StringComparison.Ordinal)))
                {
                    await File.WriteAllLinesAsync(Path.Combine(directory, "language-failure.log"), service.DrainLog());
                    throw new Exception(project.DeviceId + "/" + project.TemplateId + ": missing completion " + expected + "; see language-failure.log");
                }
            }
            var function = hal ? "HAL_GPIO_WritePin" : ll ? "LL_GPIO_SetOutputPin" : "GPIO_SetBits";
            var text = prefix + "void example(void) { " + function + "(0, 0" + (hal ? ", 0" : "") + "); }\n";
            var locations = await service.NavigateAsync("src/main.c", text, text.IndexOf(function, StringComparison.Ordinal) + 2, true);
            if (locations.Count == 0)
            {
                throw new Exception("Missing library declaration navigation");
            }
            var standard = "#include <stdint.h>\nuint32_t value;\n";
            var types = await service.NavigateAsync("src/main.c", standard, standard.IndexOf("uint32_t", StringComparison.Ordinal) + 2, true);
            if (types.Count == 0 || !(await service.ReadNavigationDocumentAsync(types[0])).IsReadOnly)
            {
                throw new Exception("Missing readonly compiler header navigation");
            }
            Console.WriteLine($"PASS language: {project.DeviceId} {project.TemplateId}, library/register/RTOS completion, declaration and standard header navigation");
            count++;
        }
        if (count == 0)
        {
            throw new Exception("No projects checked");
        }
        await File.WriteAllTextAsync(Path.Combine(root, "language-result.txt"), $"PASS {count} projects; library/register/RTOS completion, declaration/standard header navigation.\n");
        return 0;
    }
}
