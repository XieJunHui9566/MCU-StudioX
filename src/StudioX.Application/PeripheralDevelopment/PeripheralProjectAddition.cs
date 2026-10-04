namespace StudioX.Application.PeripheralDevelopment;

using StudioX.Application.Editing;

/// <summary>源码和依赖作为一个计划复核；依赖已齐全的文件仍参与过期检查。</summary>
public sealed record PeripheralProjectAddition(PeripheralDevelopmentContext Context,
    IReadOnlyList<WorkspaceFileChange> Files, IReadOnlyList<string> AddedComponents)
{
    public IReadOnlyList<WorkspaceFileChange> Changes => Files.Where(file => file.Before != file.After).ToArray();
    public string Summary => AddedComponents.Count == 0 ? "所需组件已齐全，将保留现有依赖。"
        : "将补齐组件：" + string.Join("、", AddedComponents) + "；现有依赖保留。";
}
