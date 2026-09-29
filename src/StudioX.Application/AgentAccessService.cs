namespace StudioX.Application;

using System.Security.Cryptography;
using System.Text;
using StudioX.Application.Mcp;
using StudioX.Foundation;

/// <summary>授权按工程保存；模式只决定是否询问，不绕过工具业务校验。</summary>
public sealed class AgentAccessService(string dataDirectory)
{
    public static bool Allows(AgentAccessMode mode, StudioXMcpPermission permission) => Enum.IsDefined(permission) &&
        (mode == AgentAccessMode.FullAccess || mode == AgentAccessMode.FullAuthorization &&
            permission is StudioXMcpPermission.FileWrite or StudioXMcpPermission.Build or StudioXMcpPermission.GitWrite);

    public async Task<AgentAccessMode> LoadAsync(string project, CancellationToken token = default)
    {
        var file = FilePath(project);
        var value = File.Exists(file) ? await JsonStore.ReadAsync<AgentAccessMode>(file, token) : AgentAccessMode.Review;
        return Enum.IsDefined(value) ? value : AgentAccessMode.Review;
    }

    public Task SaveAsync(string project, AgentAccessMode mode, CancellationToken token = default)
    {
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return JsonStore.WriteAsync(FilePath(project), mode, token);
    }

    private string FilePath(string project)
    {
        var identity = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)).ToUpperInvariant();
        return Path.Combine(dataDirectory, "agent-access", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))) + ".json");
    }
}
