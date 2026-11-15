namespace StudioX.WorkspaceEditingValidation;

using System.Security.Cryptography;
using StudioX.Application.CodeIntelligence;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>实际 clangd 验证标准头文件组合及真实错误；原工程源码始终只读。</summary>
internal static class StandardHeaderAnalysisChecks
{
    internal static async Task RunAsync(string runtime, string project, string output, bool coreOnly = false)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use a new output directory.");
        }
        Directory.CreateDirectory(output);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        var path = "src/main.c";
        var file = Path.Combine(project, path);
        var original = await File.ReadAllBytesAsync(file, token);
        var text = await File.ReadAllTextAsync(file, token);
        string[] headers = ["stdint.h", "stdio.h", "stddef.h", "stdlib.h", "string.h", "stdarg.h", "stdbool.h", "inttypes.h", "limits.h", "float.h", "ctype.h", "math.h", "time.h", "assert.h", "errno.h", "setjmp.h", "signal.h", "iso646.h", "wchar.h", "uchar.h"];
        if (coreOnly)
        {
            headers = headers[..7];
        }
        const string probe = "\nstruct record { uint8_t tag; uint16_t value; };\n" +
            "size_t header_probe(const char *s) { return strlen(s) + offsetof(struct record, value); }\n" +
            "void format_probe(char *buffer, uint16_t value) { sprintf(buffer, \"%u\", (unsigned int)value); }\n" +
            "int variadic_probe(int first, ...) { va_list args; va_start(args, first); int next = va_arg(args, int); va_end(args); return next; }\n";
        var cases = new List<(string Name, string Source, bool Error)>
        {
            ("actual-project", text, false),
            ("stdint-before-stdio", "#include <stdint.h>\n#include <stdio.h>\n" + text, false),
            ("all-standard-headers", string.Join('\n', headers.Select(header => "#include <" + header + ">")) + probe, false),
            ("reversed-standard-headers", string.Join('\n', headers.Reverse().Select(header => "#include <" + header + ">")) + probe, false),
            ("real-undefined-name", "#include <stdint.h>\nvoid probe(void) { studiox_missing_symbol = 1; }\n", true),
            ("real-negative-array", "#include <stdio.h>\nint negative_array[-1];\n", true),
            ("real-library-argument-error", "#include <string.h>\nvoid probe(void) { strlen(); }\n", true),
            ("real-missing-header", "#include <studiox_missing_header.h>\n", true),
            ("restored-project", text, false)
        };
        if ((await ProjectService.ReadAsync(project, token)).Kind == ProjectKind.Pic)
        {
            cases.AddRange([
                ("pic-compiler-builtins", "#include <xc.h>\nvoid probe(void) { _delaywdt(40UL); _delay(10UL); }\n", false),
                ("pic-delay-macros", "#define _XTAL_FREQ 4000000UL\n#include <xc.h>\nvoid probe(void) { __delay_ms(1); __delay_us(1); __delaywdt_ms(1); __delaywdt_us(1); }\n", false),
                ("pic-missing-clock-definition", "#include <xc.h>\nvoid probe(void) { __delay_ms(1); }\n", true),
                ("pic-invalid-builtin-arguments", "#include <xc.h>\nvoid probe(void) { _delaywdt(); }\n", true)
            ]);
        }
        var records = new List<object>();
        var failures = new List<string>();
        await using var service = new CodeIntelligenceService(runtime, Path.Combine(output, "language"));
        try
        {
            await service.StartAsync(project, token);
            foreach (var item in cases)
            {
                await service.SynchronizeDiagnosticsAsync(path, item.Source, [new(path, item.Source)], token);
                CodeDiagnosticBatch? batch = null;
                for (var attempt = 0; attempt < 150; attempt++)
                {
                    batch = service.GetDiagnostics().SingleOrDefault(value => value.Path == path && value.Text == item.Source);
                    if (batch is { IsComplete: true })
                    {
                        break;
                    }
                    await Task.Delay(40, token);
                }
                var passed = batch is { IsComplete: true } && batch.Items.Any(value => value.Severity == 1) == item.Error;
                records.Add(new
                {
                    name = item.Name,
                    passed,
                    expectedError = item.Error,
                    diagnostics = batch?.Items
                });
                if (!passed)
                {
                    failures.Add(item.Name);
                }
                Console.WriteLine((passed ? "PASS " : "FAIL ") + item.Name);
            }
            if (!(await File.ReadAllBytesAsync(file, token)).AsSpan().SequenceEqual(original))
            {
                throw new InvalidOperationException("Source changed during read-only analysis.");
            }
        }
        finally
        {
            await service.StopAsync();
            await File.WriteAllLinesAsync(Path.Combine(output, "clangd.log"), service.DrainLog());
            await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
            {
                passed = failures.Count == 0 && records.Count == cases.Count,
                records,
                sourceSha256 = Convert.ToHexString(SHA256.HashData(original)),
                hardware = false
            });
        }
        if (failures.Count > 0)
        {
            throw new InvalidOperationException("Standard header analysis failed: " + string.Join(", ", failures));
        }
    }
}
