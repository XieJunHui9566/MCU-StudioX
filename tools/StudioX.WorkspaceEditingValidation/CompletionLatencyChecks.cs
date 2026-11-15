namespace StudioX.WorkspaceEditingValidation;

using System.Diagnostics;
using System.Text.Json;
using StudioX.Application.CodeIntelligence;

internal static class CompletionLatencyChecks
{
    internal static async Task RunAsync(CodeIntelligenceService service, string header, string output, Action<bool, string> check)
    {
        var elapsed = new List<double>();
        for (var index = 0; index < 6; index++)
        {
            var text = "#include \"shared.h\"\nvoid probe(void)\n{\n    int local_probe_" + index + ";\n    local_pr\n}\n";
            var offset = text.LastIndexOf("local_pr", StringComparison.Ordinal) + "local_pr".Length;
            var watch = Stopwatch.StartNew();
            var result = await service.CompleteAsync("src/main.c", text, offset, documents: [new("src/main.c", text), new("include/shared.h", header)]);
            elapsed.Add(watch.Elapsed.TotalMilliseconds);
            check(result.Any(item => item.FilterText == "local_probe_" + index), "completion uses the latest unsaved local declaration " + index);
        }
        const string memberText = "#include \"shared.h\"\nvoid probe(struct LiveRecord *record)\n{\n    record->\n}\n";
        var memberOffset = memberText.IndexOf("record->", StringComparison.Ordinal) + "record->".Length;
        foreach (var member in new[] { "first_member", "second_member" })
        {
            var changedHeader = header + "struct LiveRecord { int " + member + "; };\n";
            var result = await service.CompleteAsync("src/main.c", memberText, memberOffset,
                documents: [new("src/main.c", memberText), new("include/shared.h", changedHeader)]);
            check(result.Any(item => item.FilterText == member) &&
                (member == "first_member" || result.All(item => item.FilterText != "first_member")),
                "fast completion refreshes unsaved header members: " + member);
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await service.CompleteAsync("src/main.c", memberText, memberOffset, cancellation.Token);
            throw new InvalidOperationException("Cancelled completion was accepted");
        }
        catch (OperationCanceledException)
        {
            check(true, "cancelled completion does not block later strict navigation");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "completion-latency.json"), JsonSerializer.Serialize(new
        {
            samplesMilliseconds = elapsed,
            warmMedianMilliseconds = elapsed.Skip(1).Order().ElementAt(2),
            automaticDebounceMilliseconds = 60,
            memberDebounceMilliseconds = 0,
            hardware = false
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
