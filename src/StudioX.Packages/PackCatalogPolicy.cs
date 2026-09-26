namespace StudioX.Packages;

public static class PackCatalogPolicy
{
    /// <summary>只把同一身份、被新版完整覆盖型号和模板的旧包视为冗余；不按名称合并不同包。</summary>
    public static bool Supersedes(PackManifest newer, PackManifest older) =>
        newer.Id == older.Id && PackVersion.Compare(newer.Version, older.Version) > 0 &&
        older.Devices.All(oldDevice => newer.Devices.Any(newDevice => newDevice.Id == oldDevice.Id &&
            oldDevice.Templates.All(oldTemplate => newDevice.Templates.Any(newTemplate => newTemplate.Id == oldTemplate.Id))));

    public static IReadOnlyList<InstalledPack> SelectCurrentVersions(IEnumerable<InstalledPack> catalog)
    {
        var result = new List<InstalledPack>();
        foreach (var group in catalog.GroupBy(pack => pack.Manifest.Id, StringComparer.Ordinal))
        {
            var retained = new List<InstalledPack>();
            foreach (var pack in group.OrderByDescending(pack => pack.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)))
                if (!retained.Any(newer => Supersedes(newer.Manifest, pack.Manifest))) retained.Add(pack);
            result.AddRange(retained);
        }
        return result.OrderBy(pack => pack.Manifest.Id, StringComparer.Ordinal)
            .ThenByDescending(pack => pack.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)).ToArray();
    }
}
