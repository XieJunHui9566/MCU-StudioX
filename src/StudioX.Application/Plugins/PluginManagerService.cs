namespace StudioX.Application.Plugins;

using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Extensions;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>管理用户插件内容、显式信任与工作区宿主；安装和发现均不执行代码。</summary>
public sealed class PluginManagerService : IAsyncDisposable
{
    private readonly string runtimeDirectory;
    private readonly string bundledDirectory;
    private readonly string userDirectory;
    private readonly string settingsPath;
    private readonly PluginRepository repository;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<PluginWorkspaceSession> sessions = [];
    private bool disposed;

    public PluginManagerService(string runtimeDirectory, string dataDirectory)
    {
        this.runtimeDirectory = Path.GetFullPath(runtimeDirectory);
        bundledDirectory = Path.Combine(this.runtimeDirectory, "plugins");
        userDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "plugins");
        settingsPath = Path.Combine(Path.GetFullPath(dataDirectory), "plugins.json");
        repository = new PluginRepository(userDirectory);
    }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<PluginCatalogEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            return await ListCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<PluginCatalogEntry> ImportAsync(string archive, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        try
        {
            ThrowIfDisposed();
            var catalog = await ListCoreAsync(cancellationToken).ConfigureAwait(false);
            var imported = await repository.ImportAsync(archive, async (manifest, token) =>
            {
                var existing = catalog.FirstOrDefault(entry => entry.Id == manifest.Id && entry.Manifest is not null);
                if (existing?.Manifest is { } current && PackVersion.Compare(manifest.Version, current.Version) < 0)
                {
                    throw new StudioXException("PLUGIN_DOWNGRADE", "已安装较高版本插件，拒绝用旧版本覆盖当前入口。");
                }
                // 仓储已完整校验 staging；撤销授权后才允许原子替换内容。
                await StopPluginAsync(manifest.Id).ConfigureAwait(false);
                var settings = await ReadSettingsAsync(token).ConfigureAwait(false);
                settings.Enabled.Remove(manifest.Id);
                await JsonStore.WriteAsync(settingsPath, settings, token).ConfigureAwait(false);
                changed = true;
            }, cancellationToken).ConfigureAwait(false);
            return (await ListCoreAsync(cancellationToken).ConfigureAwait(false)).Single(entry => entry.Id == imported.Manifest.Id);
        }
        finally
        {
            gate.Release();
            if (changed)
            {
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>启用即信任当前校验内容以当前用户权限运行；宿主操作仍受应用授权约束。</summary>
    public async Task SetEnabledAsync(string id, bool enabled, CancellationToken cancellationToken = default)
    {
        PackValidator.Token(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var catalog = await ListCoreAsync(cancellationToken).ConfigureAwait(false);
            var entry = catalog.FirstOrDefault(item => item.Id == id)
                ?? throw new StudioXException("PLUGIN_NOT_FOUND", "未发现插件：" + id);
            if (enabled && !entry.CanEnable)
            {
                throw new StudioXException("PLUGIN_INVALID", entry.Diagnostic ?? "插件无效。");
            }
            var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            await StopPluginAsync(id).ConfigureAwait(false);
            if (enabled)
            {
                settings.Enabled[id] = await FingerprintAsync(entry.ManifestPath, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                settings.Enabled.Remove(id);
            }
            await JsonStore.WriteAsync(settingsPath, settings, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UninstallAsync(string id, CancellationToken cancellationToken = default)
    {
        PackValidator.Token(id);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var directory = PathBoundary.Resolve(userDirectory, id);
            if (!Directory.Exists(directory))
            {
                throw new StudioXException("PLUGIN_BUNDLED", "内置插件只能禁用，不能从安装目录卸载。");
            }
            await StopPluginAsync(id).ConfigureAwait(false);
            var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
            settings.Enabled.Remove(id);
            await JsonStore.WriteAsync(settingsPath, settings, cancellationToken).ConfigureAwait(false);
            await repository.UninstallAsync(id, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<PluginWorkspaceSession> OpenWorkspaceAsync(string absoluteProject,
        Func<string, string, JsonElement, CancellationToken, Task<JsonElement>> broker,
        CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(absoluteProject) || !Directory.Exists(absoluteProject))
        {
            throw new StudioXException("PLUGIN_PROJECT", "插件会话需要存在的绝对工程目录。");
        }
        ArgumentNullException.ThrowIfNull(broker);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        PluginWorkspaceSession? session = null;
        try
        {
            ThrowIfDisposed();
            var catalog = await ListCoreAsync(cancellationToken).ConfigureAwait(false);
            session = new PluginWorkspaceSession(Path.GetFullPath(absoluteProject), FindHostExecutable(), broker);
            sessions.RemoveWhere(item => item.IsDisposed);
            sessions.Add(session);
            await session.StartAsync(catalog.Where(entry => entry.Enabled && entry.Manifest?.ApiVersion == 2),
                cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            if (session is not null)
            {
                sessions.Remove(session);
                await session.DisposeAsync().ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
            foreach (var session in sessions)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            sessions.Clear();
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<IReadOnlyList<PluginCatalogEntry>> ListCoreAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken).ConfigureAwait(false);
        var candidates = new List<PluginCatalogEntry>();
        foreach (var (root, bundled) in new[] { (bundledDirectory, true), (userDirectory, false) })
        {
            try
            {
                CheckAncestors(root);
            }
            catch (StudioXException ex)
            {
                candidates.Add(new(bundled ? "invalid.bundled-root" : "invalid.user-root", null,
                    Path.Combine(root, "plugin.json"), bundled, false, false, ex.ToString()));
                continue;
            }
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(directory).StartsWith('.'))
                {
                    continue;
                }
                var manifests = new[] { Path.Combine(directory, "plugin.json") };
                foreach (var path in manifests)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        _ = PathBoundary.Resolve(root, Path.GetFileName(directory));
                        var manifest = await PluginManifest.ReadAsync(path, cancellationToken).ConfigureAwait(false);
                        var fingerprint = await FingerprintAsync(path, cancellationToken).ConfigureAwait(false);
                        var enabled = settings.Enabled.TryGetValue(manifest.Id, out var trusted) && trusted == fingerprint;
                        candidates.Add(new PluginCatalogEntry(manifest.Id, manifest, path, bundled, enabled, true, null)
                        {
                            ContentFingerprint = fingerprint
                        });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        candidates.Add(new(Path.GetFileName(directory), null, path, bundled, false, false, ex.ToString()));
                    }
                }
            }
        }
        // 用户目录同 ID 优先仅用于同版本；低版本不能悄悄隐藏内置新版本。
        var selected = candidates.GroupBy(entry => entry.Id, StringComparer.Ordinal).Select(group => group
            .OrderByDescending(entry => entry.Manifest is not null)
            .ThenByDescending(entry => entry.Manifest?.Version ?? "0.0.0", Comparer<string>.Create(PackVersion.Compare))
            .ThenBy(entry => entry.IsBundled).First()).OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray();
        var authorized = selected.Where(entry => entry.Enabled).Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in sessions.SelectMany(session => session.Contributions).Select(item => item.Id).Distinct(StringComparer.Ordinal))
        {
            var current = selected.FirstOrDefault(entry => entry.Id == id);
            if (!authorized.Contains(id) || current is null || sessions.Any(session => !session.MatchesCatalogEntry(current)))
            {
                await StopPluginAsync(id).ConfigureAwait(false);
            }
        }
        return selected;
    }

    private async Task<PluginSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        CheckAncestors(Path.GetDirectoryName(settingsPath)!);
        if (new FileInfo(settingsPath).LinkTarget is not null ||
            (File.Exists(settingsPath) && (File.GetAttributes(settingsPath) & FileAttributes.ReparsePoint) != 0))
        {
            throw new StudioXException("PLUGIN_SETTINGS", "插件授权设置不能为符号链接或重解析点。");
        }
        if (!File.Exists(settingsPath))
        {
            return new(1, new(StringComparer.Ordinal));
        }
        if (new FileInfo(settingsPath).Length > 256 * 1024)
        {
            throw new StudioXException("PLUGIN_SETTINGS", "插件设置文件超过大小限制。");
        }
        var settings = await JsonStore.ReadAsync<PluginSettings>(settingsPath, cancellationToken).ConfigureAwait(false);
        if (settings.FormatVersion != 1 || settings.Enabled is null || settings.Enabled.Count > 512 ||
            settings.Enabled.Any(pair => pair.Value is null || pair.Value.Length != 64 || !pair.Value.All(Uri.IsHexDigit)))
        {
            throw new StudioXException("PLUGIN_SETTINGS", "插件设置格式无效。");
        }
        foreach (var id in settings.Enabled.Keys)
        {
            PackValidator.Token(id);
        }
        return settings;
    }

    private async Task StopPluginAsync(string id)
    {
        foreach (var session in sessions)
        {
            await session.StopPluginAsync(id).ConfigureAwait(false);
        }
    }

    private string FindHostExecutable()
    {
        var installation = Directory.GetParent(runtimeDirectory)?.FullName ?? runtimeDirectory;
        var candidates = new[]
        {
            Path.Combine(runtimeDirectory, "plugin-host", "StudioX.PluginHost.exe"),
            Path.Combine(installation, "plugin-host", "StudioX.PluginHost.exe"),
            Path.Combine(installation, "StudioX.PluginHost.exe"),
            Path.Combine(AppContext.BaseDirectory, "StudioX.PluginHost.exe")
        };
        // 工程引用可能只复制根目录 EXE；必须选择包含托管入口与运行清单的完整宿主。
        return candidates.FirstOrDefault(candidate => File.Exists(candidate) &&
            new[] { ".dll", ".deps.json", ".runtimeconfig.json" }.All(suffix =>
                File.Exists(Path.Combine(Path.GetDirectoryName(candidate)!, "StudioX.PluginHost" + suffix)))) ?? candidates[0];
    }

    private static async Task<string> FingerprintAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void CheckAncestors(string directory)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(directory)); current is not null; current = current.Parent)
        {
            if (current.LinkTarget is not null ||
                (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
            {
                throw new StudioXException("PLUGIN_LINK", "插件目录和授权设置不能经过链接目录：" + current.FullName);
            }
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    private sealed record PluginSettings(int FormatVersion, Dictionary<string, string> Enabled);
}
