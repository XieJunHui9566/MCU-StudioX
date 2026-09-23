using StudioX.Application.CodeIntelligence;
using StudioX.Engine;

internal static class LanguageChecks
{
    public static async Task<int> RunAsync(string runtime, string root)
    {
        var count = 0;
        foreach (var directory in Directory.GetDirectories(Path.GetFullPath(root)).Where(d => File.Exists(Path.Combine(d, ".studiox/project.json"))))
        {
            var project = await ProjectService.ReadAsync(directory);
            var hal = project.TemplateId.StartsWith("hal", StringComparison.Ordinal);
            var rtos = project.TemplateId.Contains("freertos", StringComparison.Ordinal);
            await using var service = new CodeIntelligenceService(Path.GetFullPath(runtime), Path.Combine(root, "language-cache"));
            await service.StartAsync(directory);
            var prefix = "#include \"board.h\"\n" + (rtos ? "#include \"FreeRTOS.h\"\n#include \"task.h\"\n" : "");
            foreach (var (partial, expected) in new[] { (hal ? "HAL_GPIO_Wri" : "GPIO_SetB", hal ? "HAL_GPIO_WritePin" : "GPIO_SetBits"), ("RCC->", "CFGR") }.Concat(rtos ? new[] { ("vTaskDel", "vTaskDelay") } : []))
            {
                var source = prefix + "void example(void) { " + partial + "\n}";
                var completions = await service.CompleteAsync("src/main.c", source, source.LastIndexOf(partial, StringComparison.Ordinal) + partial.Length);
                if (!completions.Any(c => c.InsertText.Contains(expected, StringComparison.Ordinal))) throw new Exception(project.DeviceId + ": missing completion " + expected);
            }
            var function = hal ? "HAL_GPIO_WritePin" : "GPIO_SetBits";
            var text = prefix + "void example(void) { " + function + "(0, 0" + (hal ? ", 0" : "") + "); }\n";
            var locations = await service.NavigateAsync("src/main.c", text, text.IndexOf(function, StringComparison.Ordinal) + 2, true);
            if (locations.Count == 0) throw new Exception("Missing library declaration navigation");
            var standard = "#include <stdint.h>\nuint32_t value;\n";
            var types = await service.NavigateAsync("src/main.c", standard, standard.IndexOf("uint32_t", StringComparison.Ordinal) + 2, true);
            if (types.Count == 0 || !(await service.ReadNavigationDocumentAsync(types[0])).IsReadOnly) throw new Exception("Missing readonly compiler header navigation");
            Console.WriteLine($"PASS language: {project.DeviceId} {project.TemplateId}, library/register/RTOS completion, declaration and standard header navigation");
            count++;
        }
        if (count == 0) throw new Exception("No projects checked");
        await File.WriteAllTextAsync(Path.Combine(root, "language-result.txt"), $"PASS {count} projects; library/register/RTOS completion, declaration/standard header navigation.\n");
        return 0;
    }
}
