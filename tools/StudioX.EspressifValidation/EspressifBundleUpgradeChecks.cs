namespace StudioX.EspressifValidation;

using System.Security.Cryptography;
using StudioX.Engine;
using StudioX.Packages;

/// <summary>验证新发行包可替换选择器中的旧模板，同时保留既有工程及其锁定内容。</summary>
internal static class EspressifBundleUpgradeChecks
{
    public static async Task RunAsync(string previousPacks, string currentPacks, string outputDirectory)
    {
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory))
        {
            throw new IOException("Validation output must be a new directory.");
        }
        Directory.CreateDirectory(outputDirectory);
        var repository = new PackRepository(Path.Combine(outputDirectory, "repository"));
        var original = await repository.ImportBundledMissingAsync(previousPacks);
        Check(original.Imported == 7 && original.Skipped == 0 && original.Failures.Count == 0,
            "previous seven bundled packs install without alteration");
        var oldPacks = await repository.ListAsync();
        var oldC3 = oldPacks.Single(pack => pack.Manifest.Id == "espressif.esp32c3");
        var existingProject = Path.Combine(outputDirectory, "existing-project");
        var project = await new ProjectService().CreateAsync(oldC3, "ESP32-C3", "hello-world", "existing_project", existingProject);
        var originalFiles = HashFiles(existingProject);

        var upgrade = await repository.ImportBundledMissingAsync(currentPacks);
        Check(upgrade.Imported == 6 && upgrade.Skipped == 1 && upgrade.Failures.Count == 0,
            "six newer official templates import while unchanged ESP8266 is skipped");
        var installed = await repository.ListAsync();
        Check(installed.Count == 13 && oldPacks.All(old => installed.Any(current =>
                current.Manifest.Id == old.Manifest.Id && current.Manifest.Version == old.Manifest.Version && current.ContentHash == old.ContentHash)),
            "upgrade does not overwrite the same version or delete original installed evidence");
        var currentCatalog = PackCatalogPolicy.SelectCurrentVersions(installed);
        Check(currentCatalog.Count == 7 && currentCatalog.All(pack =>
                pack.Manifest.Version == (pack.Manifest.Id == "espressif.esp8266" ? "0.1.0" : "0.1.1")),
            "device catalog selects official 0.1.1 templates and unchanged legacy 0.1.0");
        Check(currentCatalog.Where(pack => pack.Manifest.Id != "espressif.esp8266").All(pack =>
                pack.Manifest.Devices.Single().Templates.All(template => template.EspressifExample is not null)),
            "every current ESP32 template declares its native official example layout");
        var repeated = await repository.ImportBundledMissingAsync(currentPacks);
        Check(repeated.Imported == 0 && repeated.Skipped == 7 && repeated.Failures.Count == 0,
            "repeated startup skips all verified current package identities");
        Check(await ProjectService.ReadAsync(existingProject) == project && originalFiles.OrderBy(file => file.Key, StringComparer.Ordinal)
                .SequenceEqual(HashFiles(existingProject).OrderBy(file => file.Key, StringComparer.Ordinal)),
            "existing user project retains its source, metadata and locked device evidence");
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "result.txt"), "PASS 7; isolated bundled upgrade; no user projects or global repository changed.\n");
    }

    private static Dictionary<string, string> HashFiles(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException(description);
        }
        Console.WriteLine("PASS " + description);
    }
}
