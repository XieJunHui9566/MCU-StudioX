namespace StudioX.Application.StcDebugging;

using StudioX.Engine;

/// <summary>按工程准确型号准备和执行监控制作；协议验证可替换为不访问硬件的实现。</summary>
public interface IMon51SetupService
{
    Task<StcIspPreparation> PrepareForProjectAsync(string project, StcIspSettings settings, CancellationToken token = default);
    Task<StcIspReport> ExecuteAsync(StcIspPreparation prepared, IProgress<string>? output, CancellationToken token = default);
}
