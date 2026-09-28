namespace StudioX.Engine;

using System.Text.RegularExpressions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>Zephyr 工程只记录板级目标；运行时与工具链以后由独立后端锁定。</summary>
public sealed record ZephyrProjectSettings(int FormatVersion, string BoardId, string BoardTarget,
    string BoardRevision, string ZephyrVersion, bool Experimental)
{
    public static async Task ValidateAsync(string directory, ProjectManifest project, CancellationToken token)
    {
        _ = await ReadValidatedManifestAsync(directory, project, token);
    }

    public static async Task<ZephyrPackManifest> ReadValidatedManifestAsync(string directory, ProjectManifest project, CancellationToken token)
    {
        Validate(project);
        var manifestPath = PathBoundary.Resolve(directory, ".studiox/zephyr-pack.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length is <= 0 or > 2 * 1024 * 1024)
        {
            throw new StudioXException("PROJECT_ZEPHYR_PACK", "Zephyr 工程的专用包清单大小无效。");
        }
        var manifest = await JsonStore.ReadAsync<ZephyrPackManifest>(manifestPath, token);
        var board = manifest.Boards?.Where(item => item.Id == project.DeviceId).ToArray();
        var templates = board is { Length: 1 } ? board[0].Templates?.Where(item => item.Id == project.TemplateId).ToArray() : null;
        if (manifest.Schema != ZephyrPackValidator.SchemaId || manifest.FormatVersion != 1 ||
            manifest.Id != project.PackId || manifest.Version != project.PackVersion ||
            manifest.ZephyrVersion != project.Zephyr!.ZephyrVersion || !manifest.Experimental ||
            board is not { Length: 1 } || templates is not { Length: 1 } ||
            board[0].BoardTarget != project.Zephyr.BoardTarget ||
            board[0].BoardRevision != project.Zephyr.BoardRevision)
        {
            throw new StudioXException("PROJECT_ZEPHYR_PACK", "Zephyr 工程的专用包清单与创建记录不一致。");
        }
        return manifest;
    }

    public static void Validate(ProjectManifest project)
    {
        if (project.Zephyr is not { } settings || settings.FormatVersion != 1 || !settings.Experimental ||
            project.Kind != ProjectKind.Zephyr || project.CubeMx is not null || project.Logic is not null ||
            project.PinMapping is not null || project.Espressif is not null ||
            project.ToolsetId is not "" || project.ToolsetVersion is not "" || project.CompilerId is not "" ||
            project.EntryFile is not "src/main.c" || project.DeviceId != settings.BoardId ||
            settings.BoardTarget is null || settings.BoardTarget.Length > 160 ||
            !Regex.IsMatch(settings.BoardTarget,
                @"^[A-Za-z0-9_][A-Za-z0-9_.-]*(?:@[A-Za-z0-9_][A-Za-z0-9_.-]*)?(?:/[A-Za-z0-9_][A-Za-z0-9_.-]*)*$",
                RegexOptions.CultureInvariant) ||
            project.PackContentHash is not { Length: 64 } || project.PackContentHash.Any(c => !Uri.IsHexDigit(c)))
        {
            throw new StudioXException("PROJECT_ZEPHYR_SETTINGS", "Zephyr 实验工程的板级目标或包记录无效。");
        }
        PackValidator.Token(project.Name);
        PackValidator.Token(project.PackId);
        PackValidator.Version(project.PackVersion);
        PackValidator.Token(project.DeviceId);
        PackValidator.Token(project.TemplateId);
        PackValidator.Token(settings.BoardRevision);
        PackValidator.Version(settings.ZephyrVersion);
    }
}
