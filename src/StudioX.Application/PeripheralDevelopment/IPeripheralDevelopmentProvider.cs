namespace StudioX.Application.PeripheralDevelopment;

using StudioX.Engine;

/// <summary>框架适配在应用层扩展；桌面只消费能力、参数和代码预览。</summary>
public interface IPeripheralDevelopmentProvider
{
    bool Supports(ProjectManifest project);
    Task<PeripheralDevelopmentContext> ReadAsync(string directory, ProjectManifest project, CancellationToken token);
    PeripheralCodePreview Generate(PeripheralDevelopmentContext context, string optionId, IReadOnlyDictionary<string, string> values);
}
