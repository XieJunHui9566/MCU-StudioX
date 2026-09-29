namespace StudioX.LabPlugins;

using StudioX.Extensions.Abstractions;

public sealed record LabResult(object Data, string CopyText, PluginPanelWidget[] Widgets);
