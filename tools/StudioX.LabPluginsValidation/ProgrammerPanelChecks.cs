using System.Text.Json;
using StudioX.Application.Plugins;
using StudioX.Extensions;
using StudioX.Foundation;
using StudioX.LabPlugins;

internal static class ProgrammerPanelChecks
{
    internal static async Task RunCompatibilityAsync(string archives, string runtime, string output, string? legacyArchive, Action<bool, string> check)
    {
        await using (var restricted = new PluginManagerService(runtime, Path.Combine(output, "restricted-data"),
            new(3, ["decode", "commands", "panels", "agentTools", "settings", "events", "languages", "debugAdapters"])))
        {
            var entry = await restricted.ImportAsync(Directory.GetFiles(archives, "studiox.bit-lab-*.studioxplugin").Single());
            check(!entry.CanEnable && entry.Diagnostic!.Contains("panelWindows", StringComparison.Ordinal) && entry.Diagnostic.Contains("programmerCalculator", StringComparison.Ordinal),
                "host missing calculator and window capabilities explains both before enabling");
        }
        if (legacyArchive is null)
        {
            return;
        }
        await using var legacy = new PluginManagerService(runtime, Path.Combine(output, "legacy-data"));
        var old = await legacy.ImportAsync(legacyArchive);
        await legacy.SetEnabledAsync(old.Id, true);
        await using var session = await legacy.OpenWorkspaceAsync(Path.Combine(output, "fixture-project"), (_, _, _, _) => throw new InvalidOperationException("Unexpected legacy host call"));
        check(session.Contributions.Count == 1 && session.LatestPanels.Values.Single().Presentation == "panel", "unchanged legacy plugin binary activates with original three-argument panel constructor");
        var response = await session.InvokeAsync(old.Id, "agentTool", "calculate", JsonSerializer.SerializeToElement(new
        {
            value = "0x12345678",
            start = 8,
            count = 8,
            operation = "inspect"
        }));
        check(response.GetProperty("ok").GetBoolean(), "unchanged legacy plugin binary still computes in isolated host");
    }

    internal static void Run(BitLabPlugin plugin, Action<bool, string> check)
    {
        var contribution = plugin.Describe();
        var panel = contribution.Panels.Single();
        var commands = contribution.Commands.Select(command => command.Id).ToHashSet(StringComparer.Ordinal);
        var state = PluginProgrammerCalculator.Parse(panel.Widgets.Single().Value!.Value);
        PluginContributionValidator.ValidatePanel(panel, commands);
        check(true, "calculator state and independent-window presentation are validated");
        void Reject(Action action, string description)
        {
            try
            {
                action();
            }
            catch (StudioXException) { check(true, description); return; }
            throw new InvalidOperationException("Expected rejection: " + description);
        }
        JsonElement Json(object data) => JsonSerializer.SerializeToElement(data, JsonStore.Options);
        Reject(() => PluginProgrammerCalculator.Parse(Json(state with { FormatVersion = 2 })), "unknown calculator format is rejected");
        Reject(() => PluginProgrammerCalculator.Parse(Json(state with { RequestId = -1 })), "negative calculator response sequence is rejected");
        Reject(() => PluginProgrammerCalculator.Parse(Json(state with { Width = 128 })), "unbounded calculator width is rejected");
        Reject(() => PluginProgrammerCalculator.Parse(Json(state with { Binary = "X" })), "malformed bit grid is rejected");
        Reject(() => PluginProgrammerCalculator.Parse(Json(state with { Start = 31, Count = 2, Width = 32 })), "out-of-word field bounds are rejected");
        Reject(() => PluginProgrammerCalculator.Parse(Json(state with { Expression = new string('1', 1025) })), "oversized calculator input is rejected");
        Reject(() => PluginContributionValidator.ValidatePanel(panel with { Presentation = "xaml" }, commands), "unknown window presentation cannot inject a UI");
        var manifest = new PluginManifest(1, 2, "test.calculator", "1.0.0", "Calculator", "plugin.dll", "Plugin", ["commands", "panels", "agentTools"], []);
        Reject(() => PluginContributionValidator.Validate(manifest, contribution), "undeclared window capability is rejected");
        Reject(() => PluginContributionValidator.Validate(manifest with { Capabilities = ["commands", "panels", "agentTools", "panelWindows"] }, contribution), "undeclared calculator capability is rejected");
        Reject(() => PluginContributionValidator.Validate(manifest with { Capabilities = ["commands", "panels", "agentTools", "panelWindows", "programmerCalculator"] }, contribution), "undeclared live calculator capability is rejected");
        PluginContributionValidator.Validate(manifest with
        {
            Capabilities = ["commands", "panels", "agentTools", "panelWindows", "programmerCalculator", "calculatorRealtime"]
        }, contribution);
        check(true, "explicit calculator and window capabilities enable validated contribution");
    }
}
