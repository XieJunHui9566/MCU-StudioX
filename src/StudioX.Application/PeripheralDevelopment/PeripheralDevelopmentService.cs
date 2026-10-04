namespace StudioX.Application.PeripheralDevelopment;

using System.Text.RegularExpressions;
using StudioX.Engine;
using StudioX.Foundation;

public sealed partial class PeripheralDevelopmentService
{
    private readonly IReadOnlyList<IPeripheralDevelopmentProvider> providers;
    public PeripheralDevelopmentService(ToolsetCatalog tools) : this([new EspIdfPeripheralProvider(tools)]) { }
    public PeripheralDevelopmentService(IReadOnlyList<IPeripheralDevelopmentProvider> providers) => this.providers = providers.ToArray();

    public Task<PeripheralDevelopmentContext> ReadAsync(string directory, CancellationToken token = default) => Task.Run(async () =>
    {
        var root = Path.GetFullPath(directory);
        var project = await ProjectService.ReadAsync(root, token).ConfigureAwait(false);
        return await Provider(project).ReadAsync(root, project, token).ConfigureAwait(false);
    }, token);

    public PeripheralCodePreview Generate(PeripheralDevelopmentContext context, string optionId, IReadOnlyDictionary<string, string> values)
        => Provider(context.Project).Generate(context, optionId, values);

    public string PrepareInsertion(PeripheralCodePreview preview, string document)
    {
        if (Regex.IsMatch(document, @"\b" + Regex.Escape(preview.InstanceName) + @"_[A-Za-z0-9_]+\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            throw new StudioXException("PERIPHERAL_NAME_CONFLICT", "当前文件已包含相同外设实例名称，请使用其他名称。注释中的旧名称也需核对。");
        }
        var newline = document.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return preview.Code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal) + newline;
    }

    public async Task ValidateAsync(PeripheralDevelopmentContext context, CancellationToken token = default)
    {
        var current = await ReadAsync(context.ProjectDirectory, token).ConfigureAwait(false);
        if (current.Project != context.Project || current.EvidenceStamp != context.EvidenceStamp)
        {
            throw new StudioXException("PERIPHERAL_STALE", "工程配置或 SDK 外设文件已变化，请重新打开外设开发辅助。");
        }
    }

    private IPeripheralDevelopmentProvider Provider(ProjectManifest project) => providers.FirstOrDefault(provider => provider.Supports(project))
        ?? throw new StudioXException("PERIPHERAL_FRAMEWORK", "当前框架尚无外设代码适配。首批支持工程锁定的 ESP-IDF 5.5.4 / 6.1.0；可继续使用通用代码模板。");
}
