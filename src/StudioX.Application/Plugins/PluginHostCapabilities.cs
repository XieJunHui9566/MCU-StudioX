namespace StudioX.Application.Plugins;

/// <summary>应用实际提供的契约能力；与插件清单的格式校验分开，发现时不执行插件。</summary>
public sealed record PluginHostCapabilities(int MaximumApiVersion, IReadOnlyList<string> Contributions)
{
    public static PluginHostCapabilities Current
    {
        get;
    } = new(3,
        Array.AsReadOnly(new[] { "decode", "commands", "panels", "agentTools", "settings", "events", "languages", "debugAdapters", "panelWindows", "programmerCalculator", "calculatorRealtime", "mcuConfigurationPreview", "projectMatching", "projectClockV1", "mcuClockDiagramV1", "mcuCodeGenerationV1", "mcuConfigurationSaveV1", "mcuClockFieldsV1", "mcuPinActionsV1" }));
}
