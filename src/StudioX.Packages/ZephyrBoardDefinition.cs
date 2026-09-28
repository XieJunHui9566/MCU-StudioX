namespace StudioX.Packages;

/// <summary>明确的板级目标和硬件修订；不从 SoC 型号推测 Zephyr board target。</summary>
public sealed record ZephyrBoardDefinition(
    string Id,
    string DisplayName,
    string Soc,
    string BoardTarget,
    string BoardRevision,
    IReadOnlyList<ZephyrProjectTemplate> Templates,
    string? DocumentationNote = null);
