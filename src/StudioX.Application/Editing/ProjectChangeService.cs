namespace StudioX.Application.Editing;

/// <summary>创建工程独占的变化会话；监听资源不进入 Desktop 或领域层。</summary>
public sealed class ProjectChangeService
{
    public ProjectChangeSession Watch(string directory) => new(directory);
}
