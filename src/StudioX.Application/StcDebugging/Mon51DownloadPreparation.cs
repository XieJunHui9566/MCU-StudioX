namespace StudioX.Application.StcDebugging;

using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Foundation;

/// <summary>明确选定的 Mon51 用户固件身份；执行前重新核对构建，不能替换为普通 ISP 下载。</summary>
public sealed record Mon51DownloadPreparation(string ProjectDirectory, string ImageSha256, string SymbolsSha256,
    string SourceStamp, int UserBytes, string BinarySha256)
{
    internal static async Task<(Mon51DownloadPreparation Preparation, StcDebugArtifact Artifact)> ReadAsync(string project, CancellationToken token)
    {
        var settings = await ProjectBuildSettings.ReadAsync(project, token);
        if (!settings.Mon51Profile)
        {
            throw new StudioXException("MON51_DOWNLOAD_PROFILE", "请启用 Mon51 调试构建和标准 CDB，重新编译后再下载调试。普通 ISP 不能替代此通道。");
        }
        var bundle = await StcDebugArtifacts.ReadAsync(project, token);
        var binary = Binary(bundle);
        if (binary.Length < 3 || !bundle.Present.Take(3).All(value => value) || binary[0] != 0x02)
        {
            throw new StudioXException("MON51_DOWNLOAD_VECTOR", "当前仅支持具有完整三字节 LJMP 复位向量的 SDCC Mon51 构建。");
        }
        _ = Mon51Symbols.Parse(bundle.SymbolsText, bundle.ProjectDirectory, bundle.SourceFiles);
        return (new(bundle.ProjectDirectory, bundle.ImageSha256, bundle.SymbolsSha256, bundle.SourceStamp,
            binary.Length, Convert.ToHexString(SHA256.HashData(binary))), bundle);
    }

    internal static byte[] Binary(StcDebugArtifact bundle)
    {
        var last = Array.FindLastIndex(bundle.Present, value => value);
        return Enumerable.Range(0, last + 1).Select(i => bundle.Present[i] ? bundle.Code[i] : (byte)0xff).ToArray();
    }
}
