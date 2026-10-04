namespace StudioX.ComponentCatalogValidation;

using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using StudioX.Application;
using StudioX.Application.Distribution;
using StudioX.Application.Tools;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>离线边界用非可执行字节；--online 只下载已公开组件到隔离目录，不启动工具或硬件。</summary>
internal static class AcquisitionChecks
{
    internal static async Task RunAsync(string output, string catalogFile, bool online, Action<bool, string> check)
    {
        var catalogBytes = await File.ReadAllBytesAsync(catalogFile);
        var signature = await File.ReadAllBytesAsync(catalogFile + ".sig");
        var environmentPath = Environment.GetEnvironmentVariable("PATH");
        var fixture = await Context.CreateAsync(Path.Combine(output, "手动 双组件"));
        var primary = await ArchiveAsync(output, "test.acquire", "1.0.0", sevenZip: true);
        var secondary = await ArchiveAsync(output, "test.extra", "1.0.0", sevenZip: false);
        var wrong = await ArchiveAsync(output, "test.extra", "2.0.0", sevenZip: true);
        var plan = await fixture.Service.InspectAsync(fixture.Project);
        var projectBytes = await File.ReadAllBytesAsync(fixture.ProjectFile);
        await Reject(() => fixture.Service.PreviewManualAsync(plan, [primary, wrong]), "TOOLS_PROJECT_IDENTITY",
            "batch manual preview rejects a newer secondary version before any installation", check);
        check(!Directory.Exists(fixture.Tools.RootDirectory), "failed batch preview publishes no component");
        await Reject(() => fixture.Service.PreviewManualAsync(plan, [primary, primary]), "TOOLS_SELECTION",
            "duplicate manual component selection is rejected", check);
        var previews = await fixture.Service.PreviewManualAsync(plan, [primary, secondary]);
        check(previews.Count == 2 && previews[0].Archive == primary && (await File.ReadAllBytesAsync(primary)).Take(6).SequenceEqual(new byte[] { 0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c }),
            "manual mode previews real 7z and existing ZIP together without a catalog");
        var pins = new DevelopmentComponentLock(1, previews.Select(p => new DevelopmentComponentPin(p.Id, p.Version, p.Host, p.CompilerId, p.Fingerprint)).ToArray());
        var lockFile = Path.Combine(fixture.Project, DevelopmentComponentLock.RelativePath);
        await JsonStore.WriteAsync(lockFile, pins);
        plan = await fixture.Service.InspectAsync(fixture.Project);
        previews = await fixture.Service.PreviewManualAsync(plan, [primary, secondary]);
        var lockBytes = await File.ReadAllBytesAsync(lockFile);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Cancel(() => fixture.Service.InstallManualAsync(plan, previews, token: cancelled.Token),
                "cancel before manual installation publishes nothing", check);
            check(!Directory.Exists(fixture.Tools.RootDirectory), "cancelled manual batch leaves component root untouched");
        }
        await JsonStore.WriteAsync(fixture.ProjectFile, fixture.Manifest with
        {
            Name = "changed_snapshot"
        });
        await Reject(() => fixture.Service.InstallManualAsync(plan, previews), "TOOLS_PROJECT_CHANGED",
            "configuration change after batch preview prevents installation", check);
        await JsonStore.WriteAsync(fixture.ProjectFile, fixture.Manifest);
        using (var cancelled = new CancellationTokenSource())
        {
            var progress = new ImmediateProgress(text => { if (text == "已安装 test.acquire/1.0.0") { cancelled.Cancel(); } });
            await Cancel(() => fixture.Service.InstallManualAsync(plan, previews, progress, cancelled.Token),
                "cancellation between components stops the remaining batch", check);
        }
        var partial = await fixture.Service.InspectAsync(fixture.Project);
        check(partial.Requirements.Single(r => r.Id == "test.acquire").State == ProjectToolState.Installed
            && partial.Requirements.Single(r => r.Id == "test.extra").State == ProjectToolState.Missing,
            "cancelled batch preserves completed component and leaves remaining component unpublished");
        var remaining = await fixture.Service.PreviewManualAsync(partial, [secondary]);
        var result = await fixture.Service.InstallManualAsync(partial, remaining);
        check(result.Installed.Count == 1 && result.Listing is null && result.Plan.Requirements.All(r => r.State == ProjectToolState.Installed),
            "manual batch resumes only the remaining exact component");
        check((await File.ReadAllBytesAsync(fixture.ProjectFile)).SequenceEqual(projectBytes) && (await File.ReadAllBytesAsync(lockFile)).SequenceEqual(lockBytes)
            && await File.ReadAllTextAsync(Path.Combine(fixture.Project, "sdkconfig")) == "preserve settings",
            "manual acquisition preserves project snapshot, content locks and sdkconfig");
        var handler = new Handler(catalogBytes, signature);
        using var distribution = new DistributionService(Path.Combine(output, "mock-cache"), handler);
        var noNetwork = await fixture.Service.AcquireFromGithubAsync(result.Plan, distribution);
        check(handler.Requests.Count == 0 && noNetwork.Installed.Count == 0,
            "already installed requirements do not request a catalog or download");
        await fixture.Tools.SetEnabledAsync("test.acquire", "1.0.0", false);
        await Reject(async () => await fixture.Service.AcquireFromGithubAsync(await fixture.Service.InspectAsync(fixture.Project), distribution), "TOOLS_PREPARATION_BLOCKED",
            "disabled exact component routes to activation without downloading a replacement", check);
        await fixture.Tools.SetEnabledAsync("test.acquire", "1.0.0", true);
        File.Move(Path.Combine(fixture.Tools.RootDirectory, "test.extra/1.0.0/probe.exe"), Path.Combine(output, "preserved-probe.exe"));
        await Reject(async () => await fixture.Service.AcquireFromGithubAsync(await fixture.Service.InspectAsync(fixture.Project), distribution), "TOOLS_PREPARATION_BLOCKED",
            "damaged component routes to repair without overwriting it", check);
        check(handler.Requests.Count == 0, "activation and repair blockers are checked before network access");

        var unavailable = await Context.CreateAsync(Path.Combine(output, "unavailable"), "stc.sdcc", "1.0.0", "sdcc-4.5.0-15242", secondary: true);
        var unavailablePlan = await unavailable.Service.InspectAsync(unavailable.Project);
        await Reject(() => unavailable.Service.AcquireFromGithubAsync(unavailablePlan, distribution), "TOOLS_CATALOG_UNAVAILABLE",
            "automatic mode never substitutes published SDCC 1.0.1 for project 1.0.0", check);
        check(handler.Requests.Count == 2 && handler.Requests.All(uri => uri.AbsoluteUri is TrustedDevelopmentCatalog.Source or TrustedDevelopmentCatalog.Source + ".sig")
            && !Directory.Exists(unavailable.Tools.RootDirectory), "unavailable exact versions request only pinned catalog and signature, then install nothing");
        handler.Signature = [];
        await Reject(() => unavailable.Service.AcquireFromGithubAsync(unavailablePlan, distribution), "CATALOG_SIGNATURE",
            "automatic acquisition refuses a missing trusted signature instead of falling back to unsigned data", check);
        handler.Signature = signature;
        handler.BeforeRequest = _ => File.WriteAllText(unavailable.ProjectFile, JsonSerializer.Serialize(unavailable.Manifest with { Name = "changed_during_read" }, JsonStore.Options));
        await Reject(() => unavailable.Service.AcquireFromGithubAsync(unavailablePlan, distribution), "TOOLS_PROJECT_CHANGED",
            "project mutation during catalog read prevents downloads and installation", check);
        handler.BeforeRequest = null;
        await JsonStore.WriteAsync(unavailable.ProjectFile, unavailable.Manifest);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var requests = handler.Requests.Count;
            await Cancel(() => unavailable.Service.AcquireFromGithubAsync(unavailablePlan, distribution, token: cancelled.Token),
                "cancelled automatic operation stops before network access", check);
            check(requests == handler.Requests.Count, "pre-cancelled acquisition performs no HTTP requests");
        }
        if (online)
        {
            var live = await Context.CreateAsync(Path.Combine(output, "public-sdcc"), "stc.sdcc", "1.0.1", "sdcc-4.5.0-15242", secondary: false);
            var before = await File.ReadAllBytesAsync(live.ProjectFile);
            using var remote = new DistributionService(Path.Combine(output, "public-cache"));
            var installed = await live.Service.AcquireFromGithubAsync(await live.Service.InspectAsync(live.Project), remote,
                new ImmediateProgress(Console.WriteLine));
            check(installed.Listing is { BuiltInTrusted: true } && installed.Installed is [{ Identity.Id: "stc.sdcc", Identity.Version: "1.0.1" }]
                && installed.Plan.Requirements.All(r => r.State == ProjectToolState.Installed),
                "real GitHub catalog, Release download, hash verification and exact SDCC installation succeed in one operation");
            var entry = installed.Listing!.Catalog.Entries.Single(e => e.Id == "stc.sdcc" && e.Version == "1.0.1");
            check(remote.DownloadState(entry).SavedBytes == entry.DownloadBytes && (await File.ReadAllBytesAsync(live.ProjectFile)).SequenceEqual(before)
                && !File.Exists(Path.Combine(live.Project, DevelopmentComponentLock.RelativePath)),
                "public acquisition preserves configuration and leaves verified archive in independent cache");
            await JsonStore.WriteAsync(Path.Combine(output, "public-acquisition.json"), installed);
        }
        check(Environment.GetEnvironmentVariable("PATH") == environmentPath, "both acquisition modes preserve process PATH");
    }

    private static async Task<string> ArchiveAsync(string output, string id, string version, bool sevenZip)
    {
        var root = Path.Combine(output, "sources", id + "-" + version);
        Directory.CreateDirectory(root);
        byte[] bytes = [1, 2, 3, 4];
        var manifest = new ToolsetManifest(1, id, version, "win-x64", "test-gcc",
            new[] { "cmake", "ninja", "gcc", "gxx", "objcopy", "size" }.ToDictionary(r => r, _ => "probe.exe"),
            new()
            {
                ["probe.exe"] = Convert.ToHexString(SHA256.HashData(bytes))
            }, "Non-executable acquisition fixture");
        await JsonStore.WriteAsync(Path.Combine(root, "toolset.json"), manifest);
        await File.WriteAllBytesAsync(Path.Combine(root, "probe.exe"), bytes);
        var file = Path.Combine(output, id + "-" + version + ".mcutoolchain");
        if (sevenZip)
        {
            await ToolchainArchiveWriter.WriteAsync(root, ["toolset.json", "probe.exe"], file);
        }
        else
        {
            using var zip = System.IO.Compression.ZipFile.Open(file, System.IO.Compression.ZipArchiveMode.Create);
            foreach (var relative in new[] { "toolset.json", "probe.exe" })
            {
                using (var stream = zip.CreateEntry(relative).Open())
                {
                    await stream.WriteAsync(await File.ReadAllBytesAsync(Path.Combine(root, relative)));
                }
            }
        }
        return file;
    }

    private static async Task Reject(Func<Task> action, string code, string label, Action<bool, string> check)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label);
        }
        catch (StudioXException error) { check(error.Code == code, label + " · " + error.Code); }
    }
    private static async Task Cancel(Func<Task> action, string label, Action<bool, string> check)
    {
        try
        {
            await action();
            throw new InvalidOperationException(label);
        }
        catch (OperationCanceledException) { check(true, label); }
    }
    private sealed class ImmediateProgress(Action<string> action) : IProgress<string>
    {
        public void Report(string value) => action(value);
    }
    private sealed record Context(string Project, string ProjectFile, ProjectManifest Manifest, ToolsetCatalog Tools, ProjectToolPreparationService Service)
    {
        internal static async Task<Context> CreateAsync(string root, string id = "test.acquire", string version = "1.0.0", string compiler = "test-gcc", bool secondary = true)
        {
            var project = Path.Combine(root, "工程");
            var file = Path.Combine(project, ".studiox/project.json");
            List<DevelopmentComponentRequirement> needs = [new(id, version, compiler)];
            if (secondary)
            {
                needs.Add(new("test.extra", "1.0.0", "test-gcc", Purpose: "additional component"));
            }
            var manifest = new ProjectManifest(1, "acquisition_fixture", "test.pack", "1.0.0", "fixture", "TestDevice", "minimal", id, version, compiler, DevelopmentComponents: needs);
            await JsonStore.WriteAsync(file, manifest);
            await File.WriteAllTextAsync(Path.Combine(project, "sdkconfig"), "preserve settings");
            var tools = new ToolsetCatalog(Path.Combine(root, "runtime/toolsets"), Path.Combine(root, "user-data"));
            var management = new ToolManagementService(tools, new PackRepository(Path.Combine(root, "packs")), new RecentProjectService(root), root);
            return new(project, file, manifest, tools, new(tools, management));
        }
    }
    private sealed class Handler(byte[] catalog, byte[] signature) : HttpMessageHandler
    {
        internal byte[] Signature { get; set; } = signature;
        internal Action<Uri>? BeforeRequest
        {
            get; set;
        }
        internal List<Uri> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            BeforeRequest?.Invoke(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(request.RequestUri!.AbsolutePath.EndsWith(".sig", StringComparison.Ordinal) ? Signature : catalog) });
        }
    }
}
