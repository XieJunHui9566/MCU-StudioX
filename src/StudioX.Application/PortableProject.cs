namespace StudioX.Application;

/// <summary>桌面可呈现的工程身份；目标和工具信息均来自已校验工程。</summary>
public sealed record PortableProject(string Directory, string Name, string Device, string Template, string Toolset, string EntryFile);
