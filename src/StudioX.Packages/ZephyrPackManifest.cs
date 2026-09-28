namespace StudioX.Packages;

/// <summary>Zephyr 专用包格式 1；与裸机器件包使用独立清单和仓库。</summary>
public sealed record ZephyrPackManifest(
    string Schema,
    int FormatVersion,
    string Id,
    string Version,
    string DisplayName,
    string Vendor,
    string ZephyrVersion,
    bool Experimental,
    IReadOnlyList<ZephyrBoardDefinition> Boards);
