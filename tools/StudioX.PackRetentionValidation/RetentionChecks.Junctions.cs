using System.Diagnostics;
using StudioX.Foundation;
using StudioX.Packages;

internal sealed partial class RetentionChecks
{
    private async Task CheckJunctionsAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Junction validation requires Windows.");
        await CheckLinkedRootAsync(false);
        await CheckLinkedRootAsync(true);
        await CheckLinkedIdentityAsync();
        await CheckLinkedPayloadAsync(false);
        await CheckLinkedPayloadAsync(true);
    }

    private async Task CheckLinkedRootAsync(bool ancestor)
    {
        var directory = Path.Combine(output, ancestor ? "junction-root-ancestor" : "junction-root");
        var externalParent = Path.Combine(directory, "external");
        var externalRepository = await CreateTwoVersionRepositoryAsync(externalParent, "fixture.root-link");
        var link = Path.Combine(directory, "linked");
        await CreateJunctionAsync(link, ancestor ? externalParent : externalRepository.RootDirectory);
        try
        {
            var linkedRoot = ancestor ? Path.Combine(link, "packs") : link;
            try
            {
                await new PackRepository(linkedRoot).PruneSupersededAsync();
                throw new InvalidOperationException("Linked pack root was accepted: " + linkedRoot);
            }
            catch (StudioXException ex) when (ex.Code == "PATH_LINK") { }
            Require((await externalRepository.ListCatalogAsync()).Count == 2, "linked root cleanup preserves external versions");
            foreach (var pack in await externalRepository.ListCatalogAsync()) await PackRepository.VerifyAsync(pack);
            Pass(ancestor ? "junction in pack-root ancestor rejected without touching target" : "junction pack root rejected without touching target");
        }
        finally { RemoveJunction(link); }
    }

    private async Task CheckLinkedIdentityAsync()
    {
        var directory = Path.Combine(output, "junction-id");
        var externalRepository = await CreateTwoVersionRepositoryAsync(Path.Combine(directory, "external"), "fixture.id-link");
        var root = Path.Combine(directory, "packs");
        Directory.CreateDirectory(root);
        var link = Path.Combine(root, "fixture.id-link");
        await CreateJunctionAsync(link, Path.Combine(externalRepository.RootDirectory, "fixture.id-link"));
        try
        {
            var result = await new PackRepository(root).PruneSupersededAsync();
            Require(result.Removed.Count == 0 && result.Failures.Count >= 1 && result.ReclaimedBytes == 0,
                "junction ID blocks deletion");
            Require((await externalRepository.ListCatalogAsync()).Count == 2, "junction ID target remains intact");
            Pass("junction in pack ID cannot lead cleanup outside repository");
        }
        finally { RemoveJunction(link); }
    }

    private async Task CheckLinkedPayloadAsync(bool replacement)
    {
        var directory = Path.Combine(output, replacement ? "junction-replacement-payload" : "junction-old-payload");
        var repository = await CreateTwoVersionRepositoryAsync(directory, "fixture.payload-link");
        var pack = (await repository.ListCatalogAsync()).Single(item => item.Manifest.Version == (replacement ? "0.10.0" : "0.9.0"));
        var external = Path.Combine(directory, "external-sentinel");
        Directory.CreateDirectory(external);
        var sentinel = Path.Combine(external, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "must remain untouched\n");
        var link = Path.Combine(pack.RootDirectory, "sdk", "foreign");
        await CreateJunctionAsync(link, external);
        try
        {
            var result = await repository.PruneSupersededAsync();
            Require(result.Removed.Count == 0 && result.Failures.Count == 1 && result.ReclaimedBytes == 0,
                "linked payload blocks deletion");
            Require(await File.ReadAllTextAsync(sentinel) == "must remain untouched\n", "linked payload external sentinel intact");
            Require((await repository.ListCatalogAsync()).Count == 2, "linked payload preserves both versions");
            Pass(replacement ? "junction in replacement SDK rejects verification and preserves old" : "junction in old payload rejects recursive deletion and preserves target");
        }
        finally { RemoveJunction(link); }
    }

    private static async Task<PackRepository> CreateTwoVersionRepositoryAsync(string directory, string id)
    {
        var repository = new PackRepository(Path.Combine(directory, "packs"));
        foreach (var version in new[] { "0.9.0", "0.10.0" })
            await repository.ImportAsync((await FixturePack.CreateAsync(Path.Combine(directory, "archives"), id, version)).Archive);
        return repository;
    }

    private static async Task CreateJunctionAsync(string link, string target)
    {
        // 只在全新隔离fixture中创建junction；无需管理员或开启开发者符号链接权限。
        if (link.Contains('"') || target.Contains('"') || Directory.Exists(link) || File.Exists(link))
            throw new ArgumentException("Invalid or existing junction fixture path.");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var start = new ProcessStartInfo("cmd.exe", $"/d /c mklink /J \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not create fixture junction.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("Junction creation failed: " + await stdout + await stderr);
        Require((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "fixture is a reparse point");
    }

    private static void RemoveJunction(string link)
    {
        // 仅删除已核实的junction入口，永不递归删除它指向的fixture目标。
        Require((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "junction cleanup path still a link");
        Directory.Delete(link, recursive: false);
    }
}
