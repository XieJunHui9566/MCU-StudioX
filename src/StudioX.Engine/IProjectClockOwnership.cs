namespace StudioX.Engine;

/// <summary>应用决定当前准确工程是否由已启用配置插件接管；领域服务不依赖插件运行时。</summary>
public interface IProjectClockOwnership
{
    Task<bool> RequiresManagedClockAsync(string root, CancellationToken token = default);
}
