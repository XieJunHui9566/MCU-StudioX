using StudioX.Packages;

internal sealed partial class RetentionChecks
{
    private async Task CheckBundlesAsync()
    {
        var directory = Path.Combine(output, "bundle-order");
        var bundle = Path.Combine(directory, "bundle");
        var old = await FixturePack.CreateAsync(bundle, "fixture.bundle", "0.9.0");
        var newer = await FixturePack.CreateAsync(bundle, "fixture.bundle", "0.10.0");
        await FixturePack.WriteBundleIndexAsync(bundle, old, newer);
        var repository = new PackRepository(Path.Combine(directory, "packs"));
        var imported = await repository.ImportBundledMissingAsync(bundle);
        Require(imported.Imported == 1 && imported.Skipped == 1 && imported.Failures.Count == 0,
            "old-first bundle index installs newest first and skips covered older");
        Require((await repository.ListCatalogAsync()).Single().Manifest.Version == "0.10.0", "bundle chooses numeric newest version");
        await repository.ImportAsync(old.Archive);
        Require((await repository.PruneSupersededAsync()).Removed.Count == 1, "manual old installation pruned");
        imported = await repository.ImportBundledMissingAsync(bundle);
        Require(imported.Imported == 0 && imported.Skipped == 2 && imported.Failures.Count == 0,
            "repeat bundle does not resurrect pruned old version");
        Require((await repository.ListCatalogAsync()).Single().Manifest.Version == "0.10.0", "repeat bundle contains only latest");
        Pass("old-first bundle index imports numeric latest; later startup does not resurrect covered old version");

        var corruptDirectory = Path.Combine(output, "bundle-corrupt-archive");
        var corruptBundle = Path.Combine(corruptDirectory, "bundle");
        old = await FixturePack.CreateAsync(corruptBundle, "fixture.bad-bundle", "0.9.0");
        newer = await FixturePack.CreateAsync(corruptBundle, "fixture.bad-bundle", "0.10.0");
        await FixturePack.WriteBundleIndexAsync(corruptBundle, old, newer);
        await using (var stream = new FileStream(newer.Archive, FileMode.Append, FileAccess.Write))
            await stream.WriteAsync(new byte[] { 0xff });
        repository = new PackRepository(Path.Combine(corruptDirectory, "packs"));
        imported = await repository.ImportBundledMissingAsync(corruptBundle);
        Require(imported.Imported == 1 && imported.Failures.Count == 1 && imported.Skipped == 0,
            "bad latest bundle archive allows usable older archive");
        var available = (await repository.ListCatalogAsync()).Single();
        Require(available.Manifest.Version == "0.9.0", "fallback installed old version");
        await PackRepository.VerifyAsync(available);
        Pass("damaged latest bundled archive preserves usable old fallback");

        var damagedDirectory = Path.Combine(output, "bundle-damaged-installed");
        var damagedBundle = Path.Combine(damagedDirectory, "bundle");
        old = await FixturePack.CreateAsync(damagedBundle, "fixture.bad-installed", "0.9.0");
        newer = await FixturePack.CreateAsync(Path.Combine(damagedDirectory, "archive"), "fixture.bad-installed", "0.10.0");
        await FixturePack.WriteBundleIndexAsync(damagedBundle, old);
        repository = new PackRepository(Path.Combine(damagedDirectory, "packs"));
        var installedNew = await repository.ImportAsync(newer.Archive);
        await File.WriteAllTextAsync(Path.Combine(installedNew.RootDirectory, "sdk", "common.c"), "broken SDK\n");
        imported = await repository.ImportBundledMissingAsync(damagedBundle);
        Require(imported.Imported == 1 && imported.Failures.Count == 1 && imported.Skipped == 0,
            "damaged installed newer SDK does not suppress old bundle");
        available = (await repository.ListCatalogAsync()).Single(pack => pack.Manifest.Version == "0.9.0");
        await PackRepository.VerifyAsync(available);
        var cleanup = await repository.PruneSupersededAsync();
        Require(cleanup.Removed.Count == 0 && cleanup.Failures.Count == 1 && Directory.Exists(available.RootDirectory),
            "cleanup retains restored old when installed newer remains damaged");
        Pass("damaged installed newer SDK permits restoring and retaining verified old fallback");

        var uniqueDirectory = Path.Combine(output, "bundle-unique-spl");
        var uniqueBundle = Path.Combine(uniqueDirectory, "bundle");
        old = await FixturePack.CreateAsync(uniqueBundle, "fixture.bundle-spl", "0.9.0", [new("fixture-chip", ["hal", "spl"])]);
        newer = await FixturePack.CreateAsync(uniqueBundle, "fixture.bundle-spl", "0.10.0");
        await FixturePack.WriteBundleIndexAsync(uniqueBundle, old, newer);
        repository = new PackRepository(Path.Combine(uniqueDirectory, "packs"));
        imported = await repository.ImportBundledMissingAsync(uniqueBundle);
        Require(imported.Imported == 2 && imported.Skipped == 0 && imported.Failures.Count == 0,
            "bundle keeps old unique SPL template alongside latest HAL");
        Require(PackCatalogPolicy.SelectCurrentVersions(await repository.ListCatalogAsync()).Count == 2,
            "unique bundled SPL remains selectable");
        Pass("bundle retains old unique SPL capability alongside newer HAL pack");
    }
}
