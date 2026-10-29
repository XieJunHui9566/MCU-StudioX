namespace StudioX.Engine.Arduino;

/// <summary>官方 Arduino 库索引的搜索条目；安装时仍需要明确选定版本。</summary>
public sealed record ArduinoLibrarySearchResult(string Name, string Version, string Description);
