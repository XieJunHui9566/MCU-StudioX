using System.Text.Json.Nodes;
using StudioX.Application;
using StudioX.Application.CodeIntelligence;
using StudioX.Application.Editing;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal static class DelayChecks
{
    internal static async Task RunAsync(string packFile, string toolRoot, string runtime, string output)
    {
        if (Directory.Exists(output))
        {
            throw new IOException("Use new output directory");
        }
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        void Check(bool value, string name)
        {
            if (!value)
            {
                throw new Exception(name);
            }
            checks.Add(name);
            Console.WriteLine("PASS " + name);
        }
        var catalog = new ToolsetCatalog(toolRoot);
        var builder = new BuildService(catalog);
        var isp = new Mx251IspService(catalog, Path.Combine(output, "data"));
        var pack = await new PackRepository(Path.Combine(output, "packs")).ImportAsync(packFile);
        var root = Path.Combine(output, "project");
        await new ProjectService().CreateAsync(pack, "AI8051U-34K64", "registers", "delay", root);
        var main = Path.Combine(root, "src/main.c");
        const string valid = """
            #include "../include/AI8051U.h"
            _Static_assert(sizeof(uint8_t)==1 && sizeof(int8_t)==1,"8-bit types");
            _Static_assert(sizeof(uint16_t)==2 && sizeof(int16_t)==2,"16-bit types");
            _Static_assert(sizeof(uint32_t)==4 && sizeof(int32_t)==4,"32-bit types");
            _Static_assert(UINT8_MAX==255 && UINT16_MAX==65535 && UINT32_MAX==4294967295UL,"integer ranges");
            volatile uint16_t count=500;
            void main(void) { for(;;) { P20=0; Delay_MS(count); P20=1; Delay_MS(count); } }
            """;
        await File.WriteAllTextAsync(main, valid);
        var configPath = Path.Combine(root, "mx251.json");
        var config = JsonNode.Parse(await File.ReadAllTextAsync(configPath))!;
        foreach (var model in new[] { "small", "large" })
        {
            foreach (var optimization in new[] { "speed", "size" })
            {
                foreach (var hz in new[] { 1000000, 11059200, 40000000, 48000000 })
                {
                    config["model"] = model;
                    config["optimization"] = optimization;
                    config["defines"] = new JsonArray("MX251_IRC_HZ=" + hz, "MX251_DELAY_TICKS=" + ((hz + 2000) / 4000 - 1));
                    await File.WriteAllTextAsync(configPath, config.ToJsonString(JsonStore.Options));
                    var built = await builder.BuildAsync(root);
                    var label = $"{model}-{optimization}-{hz}";
                    await File.WriteAllTextAsync(Path.Combine(output, label + ".log"), built.Log);
                    Check(built.Success, label + " compile uintN_t and Delay_MS");
                    var evidenceDirectory = Path.GetFullPath(Path.Combine(output, "code-evidence", label));
                    Directory.CreateDirectory(evidenceDirectory);
                    foreach (var file in new[] { "firmware.hex", "ai8051u_delay.lst", "ai8051u_delay.asm", "main.asm", "receipt.json", "commands.json" })
                    {
                        File.Copy(Path.Combine(Path.GetDirectoryName(built.Artifacts[0])!, file), Path.Combine(evidenceDirectory, file));
                    }
                    await JsonStore.WriteAsync(Path.Combine(output, label + ".json"), new
                    {
                        model,
                        optimization,
                        hz,
                        built.Artifacts,
                        evidenceDirectory
                    });
                }
            }
        }
        Check(Directory.GetDirectories(Path.Combine(root, ".build/mx251")).Select(Path.GetFileName).SequenceEqual(new[] { "latest" }), "build cache retains only latest");
        Check(File.ReadAllBytes(Path.Combine(root, ".build/firmware.hex")).SequenceEqual(File.ReadAllBytes(Path.Combine(root, ".build/mx251/latest/firmware.hex"))), "fixed public firmware is byte-identical to validated image");
        using (var held = new FileStream(Path.Combine(root, ".studiox/locks/mx251-operation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            bool busy = false;
            try
            {
                await builder.BuildAsync(root);
            }
            catch (StudioXException error) when (error.Code == "MX251_BUSY") { busy = true; }
            Check(busy && File.Exists(Path.Combine(root, ".build/firmware.hex")), "second process cannot clean a busy project or invalidate its result");
        }
        await isp.SaveSettingsAsync(root, new(IrcHz: 24000000));
        Check(!File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")) && !File.Exists(Path.Combine(root, ".build/firmware.hex")), "saved clock invalidates old build receipt and public firmware");
        var otherPc = new Mx251IspService(catalog, Path.Combine(output, "another-pc"));
        Check((await otherPc.LoadSettingsAsync(root)).IrcHz == 24000000, "clock follows project to another user-data directory");
        Check((await builder.BuildAsync(root)).Success, "rebuild after clock save");
        await isp.SaveSettingsAsync(root, new(IrcHz: 24000000));
        Check(File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")), "saving unchanged clock preserves valid receipt");

        const string broken = "void bad(void) __naked { volatile unsigned long i=9; while(i) i--; }\nvoid main(void){ bad(); for(;;); }\n";
        await File.WriteAllTextAsync(main, broken);
        bool rejected = false;
        try
        {
            var result = await builder.BuildAsync(root);
            rejected = !result.Success && result.Log.Contains("MX251_NAKED_FALLTHROUGH");
        }
        catch (StudioXException error) when (error.Code == "MX251_NAKED_FALLTHROUGH") { rejected = true; }
        Check(rejected && !File.Exists(Path.Combine(root, ".build/studiox-build-receipt.json")), "naked C fallthrough rejected without valid firmware receipt");
        var failedRun = Directory.GetDirectories(Path.Combine(root, ".build/mx251")).Order().Last();
        Check(!File.Exists(Path.Combine(failedRun, "firmware.hex")) && !File.Exists(Path.Combine(root, ".build/firmware.hex")), "unsafe final and public hex removed, original diagnostics retained");
        foreach (var (body, expected) in new[] { ("__asm eret __endasm;", true), ("__asm sjmp . __endasm;", true),
                     ("__asm je 00001$\n eret\n00001$:\n nop\n__endasm;", false) })
        {
            await File.WriteAllTextAsync(main, "void bad(void) __naked { " + body + " }\nvoid main(void){ bad(); for(;;); }");
            bool passed;
            try
            {
                passed = (await builder.BuildAsync(root)).Success;
            }
            catch (StudioXException e) when (e.Code == "MX251_NAKED_FALLTHROUGH") { passed = false; }
            Check(passed == expected, "naked control flow " + body);
        }
        await File.WriteAllTextAsync(main, valid);
        await using var editor = new CodeIntelligenceService(runtime, Path.Combine(output, "language-data"));
        await editor.StartAsync(root);
        async Task<CodeDiagnosticBatch> Diagnostics(string source, IReadOnlyList<CodeDocumentSnapshot>? docs = null)
        {
            await editor.SynchronizeDiagnosticsAsync("src/main.c", source, docs ?? [new("src/main.c", source)]);
            for (var i = 0; i < 100; i++)
            {
                var batch = editor.GetDiagnostics().FirstOrDefault(b => b.Path == "src/main.c" && b.Text == source);
                if (batch is not null)
                {
                    return batch;
                }
                await Task.Delay(100);
            }
            throw new Exception("clangd diagnostics timeout: " + string.Join("\n", editor.DrainLog()));
        }
        var withAsm = valid + "\nvoid instructions(void){ NOP1(); NOP40(); __asm nop __endasm; }\n";
        var good = await Diagnostics(withAsm);
        await JsonStore.WriteAsync(Path.Combine(output, "editor-good.json"), good);
        Check(good.Items.All(d => d.Severity != 1), "actual clangd accepts NOP macros, inline asm and clock definitions");
        var headerPath = "include/AI8051U.h";
        var header = await File.ReadAllTextAsync(Path.Combine(root, headerPath));
        var brokenHeader = header.Replace("#define NOP1() do { __asm nop __endasm; } while (0)", "#define NOP1() do { missing_in_unsaved_header = 1; } while (0)");
        var unsaved = await Diagnostics(withAsm, [new("src/main.c", withAsm), new(headerPath, brokenHeader)]);
        Check(unsaved.Items.Any(d => d.Severity == 1 && d.Message.Contains("missing_in_unsaved_header")), "unsaved header C errors are not replaced by disk overlay");
        var restored = await Diagnostics(withAsm);
        Check(restored.Items.All(d => d.Severity != 1), "closing unsaved header restores original NOP macro projection");
        var literal = await Diagnostics(withAsm + "\nconst char *literal=\"__asm fake __endasm\"; /* __asm ignored __endasm */\n");
        Check(literal.Items.All(d => d.Severity != 1), "asm tokens inside strings and comments remain C text");
        var originalDocument = await new ProjectFileService().ReadAsync(root, "src/main.c");
        bool safeFormat;
        try
        {
            var edits = await editor.FormatAsync(new WorkspaceBufferSnapshot(originalDocument, withAsm), null, 0, [new("src/main.c", withAsm)]);
            safeFormat = edits.All(e => e.After.Contains("__asm nop __endasm", StringComparison.Ordinal));
        }
        catch (StudioXException error) when (error.Code == "SDCC_ASM_EDIT") { safeFormat = true; }
        Check(safeFormat, "formatting cannot replace real assembly with analysis whitespace");
        var bad = await Diagnostics(withAsm + "\nvoid wrong(void){ unknown_identifier=1; }\n");
        Check(bad.Items.Any(d => d.Severity == 1 && d.Message.Contains("unknown_identifier")), "real C error after assembly remains visible");
        var typo = await Diagnostics(valid + "\nvoid typo(void){ __asm nop __endasmv; }\n");
        Check(typo.Items.Any(d => d.Severity == 1), "misspelled asm terminator is not hidden");
        var signatureText = valid + "\nvoid call(void){ Delay_MS(500); }\n";
        var signature = await editor.SignatureAsync("src/main.c", signatureText, signatureText.LastIndexOf("Delay_MS(", StringComparison.Ordinal) + 9);
        Check(signature?.Label.Contains("Delay_MS", StringComparison.Ordinal) == true, "Delay_MS signature available");
        await File.WriteAllLinesAsync(Path.Combine(output, "editor.log"), editor.DrainLog());
        Check(await File.ReadAllTextAsync(main) == valid, "editor projection never edits disk source");
        await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new
        {
            checks,
            hardwareOpened = false
        });
    }
}
