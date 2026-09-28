namespace StudioX.Packages;

using System.Text.RegularExpressions;
using StudioX.Foundation;

/// <summary>只接受明确板目标与模板文件映射的 Zephyr 实验包。</summary>
public static partial class ZephyrPackValidator
{
    public const string SchemaId = "studiox.zephyr-pack";
    public const string ManifestFile = "zephyr-manifest.json";
    public const string HashIndex = "files.sha256.json";

    /// <summary>完整校验清单和全部被引用的模板资源。</summary>
    public static void Validate(ZephyrPackManifest manifest, string root) => ValidateCore(manifest, root, requireFiles: true);

    // 目录展示只读取安装记录、索引与清单；创建工程前仓库必须再次完整校验。
    internal static void ValidateCatalog(ZephyrPackManifest manifest, string root) => ValidateCore(manifest, root, requireFiles: false);

    private static void ValidateCore(ZephyrPackManifest manifest, string root, bool requireFiles)
    {
        if (manifest.Schema != SchemaId || manifest.FormatVersion != 1)
        {
            throw new StudioXException("ZEPHYR_PACK_FORMAT", "需要 Zephyr 专用包格式 1；普通 StudioX 芯片包不能作为 Zephyr 包导入。");
        }
        PackValidator.Token(manifest.Id);
        PackValidator.Version(manifest.Version);
        PackValidator.Version(manifest.ZephyrVersion);
        RequireText(manifest.DisplayName, 160, "包显示名称");
        RequireText(manifest.Vendor, 160, "厂商名称");
        if (!manifest.Experimental)
        {
            throw new StudioXException("ZEPHYR_PACK_EXPERIMENTAL", "Zephyr 包格式 1 必须明确标记为实验模式。");
        }
        if (manifest.Boards is not { Count: > 0 })
        {
            throw new StudioXException("ZEPHYR_PACK_BOARD", "Zephyr 包没有明确的板级目标。");
        }
        var boardIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var boardTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var board in manifest.Boards)
        {
            if (board is null)
            {
                throw new StudioXException("ZEPHYR_PACK_BOARD", "Zephyr 包包含空板卡定义。");
            }
            PackValidator.Token(board.Id);
            PackValidator.Token(board.Soc);
            PackValidator.Token(board.BoardRevision);
            RequireText(board.DisplayName, 160, "板卡显示名称");
            if (board.BoardTarget is null || board.BoardTarget.Length > 160 || !BoardTargetPattern().IsMatch(board.BoardTarget) ||
                !boardIds.Add(board.Id) || !boardTargets.Add(board.BoardTarget))
            {
                throw new StudioXException("ZEPHYR_PACK_BOARD", "板卡 ID 或精确 Zephyr board target 无效或重复。");
            }
            if (board.DocumentationNote is not null)
            {
                RequireText(board.DocumentationNote, 1024, "板卡资料说明");
            }
            if (board.Templates is not { Count: > 0 })
            {
                throw new StudioXException("ZEPHYR_PACK_TEMPLATE", "板卡没有工程模板。");
            }
            var templateIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var template in board.Templates)
            {
                if (template is null)
                {
                    throw new StudioXException("ZEPHYR_PACK_TEMPLATE", "Zephyr 包包含空模板定义。");
                }
                PackValidator.Token(template.Id);
                RequireText(template.DisplayName, 160, "模板显示名称");
                RequireText(template.Description, 1024, "模板说明");
                if (!templateIds.Add(template.Id) || template.Files is not { Count: > 0 })
                {
                    throw new StudioXException("ZEPHYR_PACK_TEMPLATE", "模板 ID 重复或缺少资源映射。");
                }
                var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var (destination, source) in template.Files)
                {
                    _ = PathBoundary.Resolve(root, destination);
                    _ = PathBoundary.Resolve(root, source);
                    if (!destinations.Add(destination) || ReservedDestination(destination) ||
                        !source.StartsWith("templates/", StringComparison.Ordinal))
                    {
                        throw new StudioXException("ZEPHYR_PACK_TEMPLATE_FILE", "Zephyr 模板目标路径或包内资源路径无效。");
                    }
                    if (requireFiles && !File.Exists(PathBoundary.Resolve(root, source)))
                    {
                        throw new StudioXException("ZEPHYR_PACK_FILE_MISSING", "Zephyr 包缺少模板资源：" + source);
                    }
                }
                if (!template.Files.Keys.Contains("CMakeLists.txt", StringComparer.Ordinal) ||
                    !template.Files.Keys.Contains("prj.conf", StringComparer.Ordinal) ||
                    !template.Files.Keys.Contains("src/main.c", StringComparer.Ordinal))
                {
                    throw new StudioXException("ZEPHYR_PACK_TEMPLATE", "Zephyr 模板必须显式提供 CMakeLists.txt、prj.conf 和 src/main.c。");
                }
            }
        }
    }

    private static bool ReservedDestination(string relative)
    {
        if (new[] { ManifestFile, HashIndex, "manifest.json", ".gitignore" }
            .Contains(relative, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }
        return relative.Split('/').Any(part => part.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".studiox", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".build", StringComparison.OrdinalIgnoreCase) ||
            part.Equals(".west", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("build", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("device", StringComparison.OrdinalIgnoreCase));
    }

    private static void RequireText(string? value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength || value.Any(char.IsControl))
        {
            throw new StudioXException("ZEPHYR_PACK_MANIFEST", name + "无效。");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9_][A-Za-z0-9_.-]*(?:@[A-Za-z0-9_][A-Za-z0-9_.-]*)?(?:/[A-Za-z0-9_][A-Za-z0-9_.-]*)*$", RegexOptions.CultureInvariant)]
    private static partial Regex BoardTargetPattern();
}
