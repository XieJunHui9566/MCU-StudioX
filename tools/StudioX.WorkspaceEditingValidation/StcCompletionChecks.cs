namespace StudioX.WorkspaceEditingValidation;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;

/// <summary>使用指定 STC 工程和实际发行运行时做只读语言验证；不启动 WPF 或固件工具。</summary>
internal static class StcCompletionChecks
{
    internal static async Task RunAsync(string runtime, string project, string output)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new output directory.");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool passed, string message)
        {
            if (!passed)
            {
                throw new InvalidOperationException(message);
            }
            checks.Add(message);
            Console.WriteLine("PASS " + message);
        }
        CompletionTriggerChecks.Run(Check);
        var main = Path.Combine(project, "src/main.c");
        var original = await File.ReadAllBytesAsync(main);
        var source = await File.ReadAllTextAsync(main);
        var records = new List<object>();
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "user-data"));
        try
        {
            await service.StartAsync(project);
            Check(service.IsReady, "real clangd starts with the trial runtime");
            foreach (var (prefix, expected) in new[] { ("P", "P11"), ("P1", "P11"), ("P11", "P11"), ("USART1_pr", "USART1_printf"), ("Delay_", "Delay_ms"), ("uint8_", "uint8_t"), ("LE", "LED") })
            {
                var text = source + "\nvoid completion_probe(void)\n{\n    " + prefix + "\n}\n";
                var offset = text.LastIndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
                var results = await service.CompleteAsync("src/main.c", text, offset);
                records.Add(new
                {
                    prefix,
                    expected,
                    results
                });
                Check(results.Any(item => item.FilterText == expected || item.Label.StartsWith(expected, StringComparison.Ordinal)), "real completion: " + prefix + " -> " + expected);
            }
            var valid = source + "\nvoid completion_probe(void) { P11 = 1; Delay_ms(1); USART1_printf(\"%u\", (unsigned int)P11); }\n";
            var signature = await service.SignatureAsync("src/main.c", valid, valid.LastIndexOf("Delay_ms(", StringComparison.Ordinal) + "Delay_ms(".Length);
            Check(signature?.Label.Contains("Delay_ms", StringComparison.Ordinal) == true && signature.Parameters.Any(parameter => parameter.Contains("uint16_t", StringComparison.Ordinal)), "Delay_ms signature exposes its uint16_t argument");
            var declaration = await service.NavigateAsync("src/main.c", valid, valid.LastIndexOf("P11", StringComparison.Ordinal) + 1, declaration: true);
            Check(declaration.Any(item => item.DocumentPath.EndsWith("stc15.h", StringComparison.OrdinalIgnoreCase)), "P11 resolves to the actual STC device header");
            await service.SynchronizeDiagnosticsAsync("src/main.c", valid, [new("src/main.c", valid)]);
            for (var attempt = 0; attempt < 50 && !service.GetDiagnostics().Any(batch => batch.Path == "src/main.c" && batch.Text == valid); attempt++)
            {
                await Task.Delay(100);
            }
            var diagnostics = service.GetDiagnostics();
            await File.WriteAllTextAsync(Path.Combine(output, "diagnostics.json"), JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
            Check(diagnostics.Any(batch => batch.Path == "src/main.c" && batch.Text == valid && batch.Items.All(item => item.Severity != 1)), "valid main has no errors from SDCC target or calling convention declarations");
            Check((await File.ReadAllBytesAsync(main)).SequenceEqual(original), "completion, signature and diagnostics do not change main.c");
            await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new
            {
                passed = true,
                checks,
                mainSha256 = Convert.ToHexString(SHA256.HashData(original)),
                desktopControlled = false,
                hardware = false
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(output, "completions.json"), JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllLinesAsync(Path.Combine(output, "language.log"), service.DrainLog());
        }
    }
}
