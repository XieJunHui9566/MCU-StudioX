namespace StudioX.Engine;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>用户工程名称与组件标识符分开校验；工具内部标识不依赖显示名称的字符集。</summary>
public static partial class ProjectName
{
    public static void Validate(string? name)
    {
        if (name is null || name.Length > 80 || !DisplayName().IsMatch(name))
        {
            throw new StudioXException("PROJECT_NAME", "工程名称支持中文、英文、数字、空格、下划线、连字符和点，最多 80 字符；首尾不能有空格，末尾不能是点。");
        }
        PathBoundary.ValidateFileName(name);
    }

    /// <summary>保留既有英文工程的产物名；中文名称使用稳定 ASCII 标识，避免进入 SDK 的符号或脚本。</summary>
    public static string BuildIdentifier(string name)
    {
        Validate(name);
        return AsciiIdentifier().IsMatch(name) ? name : "firmware_" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..16].ToLowerInvariant();
    }

    [GeneratedRegex(@"\A[\p{L}\p{N}_][\p{L}\p{M}\p{N}_. -]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex DisplayName();

    [GeneratedRegex(@"\A[A-Za-z0-9_][A-Za-z0-9_.-]*\z", RegexOptions.CultureInvariant)]
    private static partial Regex AsciiIdentifier();
}
