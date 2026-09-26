using System.Security.Cryptography;
using StudioX.Application;
using StudioX.Engine;
using StudioX.Foundation;
using StudioX.Packages;

internal sealed partial class RetentionChecks(string output)
{
    private readonly List<string> passed = [];

    public async Task RunAsync()
    {
        CheckPolicy();
        await CheckPruningAndProjectAsync();
        await CheckDistinctCapabilitiesAsync();
        await CheckDamagedReplacementAsync();
        await CheckCancellationAsync();
        await CheckBundlesAsync();
        await CheckJunctionsAsync();
        await File.WriteAllLinesAsync(Path.Combine(output, "result.txt"), passed.Select(item => "PASS: " + item));
        Console.WriteLine($"PASS: {passed.Count} pack retention checks; fixtures only; no hardware or firmware build.");
    }

    private void CheckPolicy()
    {
        Require(PackVersion.Compare("0.9.0", "0.10.0") < 0, "numeric minor version order");
        Require(PackVersion.Compare("99999999999999999999999999999.0.0", "100000000000000000000000000000.0.0") < 0,
            "unbounded integer version segments");
        Require(PackVersion.Compare("0.10.99999999999999999999999999999", "0.10.100000000000000000000000000000") < 0,
            "unbounded patch version segments");
        Require(PackVersion.Compare("100000000000000000000000000000.10.0", "100000000000000000000000000000.10.0") == 0,
            "equal large versions");
        Pass("numeric versions including 0.9/0.10 and values exceeding UInt64");
    }

    private async Task CheckPruningAndProjectAsync()
    {
        var directory = Path.Combine(output, "prune-project");
        var old = await FixturePack.CreateAsync(Path.Combine(directory, "archives"), "fixture.same", "0.9.0");
        var newer = await FixturePack.CreateAsync(Path.Combine(directory, "archives"), "fixture.same", "0.10.0");
        var repository = new PackRepository(Path.Combine(directory, "packs"));
        var installedOld = await repository.ImportAsync(old.Archive);
        var projectDirectory = Path.Combine(directory, "old-project");
        var project = await new ProjectService().CreateAsync(installedOld, "fixture-chip", "hal", "old_project", projectDirectory);
        var copiedSdk = Path.Combine(projectDirectory, "device", "sdk", "common.c");
        var originalSdkHash = await HashAsync(copiedSdk);
        await repository.ImportAsync(newer.Archive);
        Require(PackCatalogPolicy.Supersedes(newer.Manifest, old.Manifest), "newer complete pack supersedes older");
        Require(!PackCatalogPolicy.Supersedes(old.Manifest, newer.Manifest), "supersedes direction follows version");
        var selected = PackCatalogPolicy.SelectCurrentVersions(await repository.ListCatalogAsync());
        Require(selected.Count == 1 && selected[0].Manifest.Version == "0.10.0", "selector retains newest fully covering pack");
        var oldInstallation = Path.GetDirectoryName(installedOld.RootDirectory)!;
        var expectedBytes = Directory.GetFiles(oldInstallation, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var result = await repository.PruneSupersededAsync();
        Require(result.Failures.Count == 0 && result.Removed.Count == 1 && result.Removed[0] is
            { Id: "fixture.same", Version: "0.9.0", ReplacementVersion: "0.10.0" }, "prune removal identity");
        Require(result.ReclaimedBytes == expectedBytes && result.Removed[0].Bytes == expectedBytes, "exact reclaimed byte accounting");
        Require(!Directory.Exists(oldInstallation), "global older installation deleted");
        var readProject = await ProjectService.ReadAsync(projectDirectory);
        var info = await ProjectDeviceInfo.ReadAsync(projectDirectory, readProject);
        Require(readProject == project && readProject.PackVersion == "0.9.0", "existing project pin stays unchanged");
        Require(info.DeviceName == "Fixture MCU fixture-chip" && info.TemplateName == "Fixture hal" && info.Notice == "",
            "project metadata remains available through its device copy");
        Require(await HashAsync(copiedSdk) == originalSdkHash, "project SDK copy remains unchanged");
        await PackRepository.VerifyAsync((await repository.ListCatalogAsync()).Single());
        var repeat = await repository.PruneSupersededAsync();
        Require(repeat.Removed.Count == 0 && repeat.Failures.Count == 0 && repeat.ReclaimedBytes == 0, "prune is idempotent");
        Pass("covered old version removed with exact bytes; pinned existing project reads unchanged device copy");
    }

    private async Task CheckDistinctCapabilitiesAsync()
    {
        var directory = Path.Combine(output, "distinct");
        var archives = Path.Combine(directory, "archives");
        var repository = new PackRepository(Path.Combine(directory, "packs"));
        var spl = await FixturePack.CreateAsync(archives, "fixture.templates", "0.9.0", [new("fixture-chip", ["hal", "spl"])]);
        var hal = await FixturePack.CreateAsync(archives, "fixture.templates", "0.10.0", [new("fixture-chip", ["hal"])]);
        var anotherId = await FixturePack.CreateAsync(archives, "fixture.different-id", "1.0.0");
        var oldDevices = await FixturePack.CreateAsync(archives, "fixture.devices", "0.9.0",
            [new("fixture-chip", ["hal"]), new("fixture-other-chip", ["hal"])]);
        var fewerDevices = await FixturePack.CreateAsync(archives, "fixture.devices", "0.10.0");
        foreach (var fixture in new[] { spl, hal, anotherId, oldDevices, fewerDevices })
            await repository.ImportAsync(fixture.Archive);
        Require(!PackCatalogPolicy.Supersedes(hal.Manifest, spl.Manifest), "unique SPL template preserves old pack");
        Require(!PackCatalogPolicy.Supersedes(fewerDevices.Manifest, oldDevices.Manifest), "missing device preserves old pack");
        Require(!PackCatalogPolicy.Supersedes(anotherId.Manifest, hal.Manifest), "same display name does not merge IDs");
        Require(PackCatalogPolicy.SelectCurrentVersions(await repository.ListCatalogAsync()).Count == 5,
            "selector keeps unique templates/devices and distinct IDs");
        var result = await repository.PruneSupersededAsync();
        Require(result.Removed.Count == 0 && result.Failures.Count == 0 && (await repository.ListCatalogAsync()).Count == 5,
            "prune keeps all distinct capabilities");
        Pass("SPL templates, unique devices and same-named different IDs are preserved");
    }

    private async Task CheckDamagedReplacementAsync()
    {
        var directory = Path.Combine(output, "damaged-sdk");
        var repository = new PackRepository(Path.Combine(directory, "packs"));
        var old = await FixturePack.CreateAsync(Path.Combine(directory, "archives"), "fixture.damage", "0.9.0");
        var newer = await FixturePack.CreateAsync(Path.Combine(directory, "archives"), "fixture.damage", "0.10.0");
        var installedOld = await repository.ImportAsync(old.Archive);
        var installedNew = await repository.ImportAsync(newer.Archive);
        await File.WriteAllTextAsync(Path.Combine(installedNew.RootDirectory, "sdk", "common.c"), "tampered SDK\n");
        var result = await repository.PruneSupersededAsync();
        Require(result.Removed.Count == 0 && result.Failures.Count == 1 && result.ReclaimedBytes == 0,
            "corrupt replacement prevents deletion and reports failure");
        Require(result.Failures[0].Message.Contains("sdk/common.c", StringComparison.Ordinal), "original changed-file diagnostic preserved");
        await PackRepository.VerifyAsync(installedOld);
        Pass("damaged replacement SDK leaves usable older version and reports original diagnostics");
    }

    private async Task CheckCancellationAsync()
    {
        var directory = Path.Combine(output, "cancelled");
        var repository = new PackRepository(Path.Combine(directory, "packs"));
        foreach (var version in new[] { "0.9.0", "0.10.0" })
            await repository.ImportAsync((await FixturePack.CreateAsync(Path.Combine(directory, "archives"), "fixture.cancel", version)).Archive);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { await repository.PruneSupersededAsync(cancellation.Token); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
        Require((await repository.ListCatalogAsync()).Count == 2, "cancelled operation preserves both versions");
        Pass("pre-cancelled cleanup preserves all installations");
    }

    private void Pass(string message) { passed.Add(message); Console.WriteLine("PASS: " + message); }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL: " + message);
    }
    private static async Task<string> HashAsync(string path) => Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)));
}
