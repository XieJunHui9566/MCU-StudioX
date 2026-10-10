namespace StudioX.Application.BuildConfiguration;

/// <summary>只修改编译登记；文件创建、删除和移动仍由原来的工程文件服务负责。</summary>
public sealed record SourceRegistrationOperation(SourceRegistrationKind Kind, string Path, string? NewPath = null)
{
    public string Description => Kind switch
    {
        SourceRegistrationKind.Add => "加入编译 · " + Path,
        SourceRegistrationKind.Remove => "移除登记 · " + Path,
        _ => "更新改名 · " + Path + " → " + NewPath
    };
}
