namespace StudioX.Packages;

public static class PackCatalogPolicy
{
    /// <summary>只清理构建身份相同且完整覆盖的旧包；不同 SDK/组件版本必须保留，供新建选择和锁定工程使用。</summary>
    public static bool Supersedes(PackManifest newer, PackManifest older) =>
        newer.Id == older.Id && PackVersion.Compare(newer.Version, older.Version) > 0 &&
        older.Devices.All(oldDevice => newer.Devices.Any(newDevice => newDevice.Id == oldDevice.Id &&
            newDevice.Espressif == oldDevice.Espressif &&
            oldDevice.Templates.All(oldTemplate => newDevice.Templates.Any(newTemplate =>
                (newTemplate.Id == oldTemplate.Id || newTemplate.ReplacesTemplates?.Contains(oldTemplate.Id, StringComparer.Ordinal) == true) &&
                SameComponents(newDevice, newTemplate, oldDevice, oldTemplate)))));

    private static bool SameComponents(DeviceDefinition newer, ProjectTemplate newTemplate, DeviceDefinition older, ProjectTemplate oldTemplate)
    {
        var newNeeds = DevelopmentComponentRequirements.ForTemplate(newer, newTemplate);
        var oldNeeds = DevelopmentComponentRequirements.ForTemplate(older, oldTemplate);
        return newNeeds.Count == oldNeeds.Count && oldNeeds.All(oldNeed => newNeeds.Any(newNeed =>
            DevelopmentComponentRequirements.SameIdentity(oldNeed, newNeed)));
    }

    public static IReadOnlyList<InstalledPack> SelectCurrentVersions(IEnumerable<InstalledPack> catalog)
    {
        var result = new List<InstalledPack>();
        foreach (var group in catalog.GroupBy(pack => pack.Manifest.Id, StringComparer.Ordinal))
        {
            var retained = new List<InstalledPack>();
            foreach (var pack in group.OrderByDescending(pack => pack.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)))
            {
                if (!retained.Any(newer => Supersedes(newer.Manifest, pack.Manifest)))
                {
                    retained.Add(pack);
                }
            }
            result.AddRange(retained);
        }
        return result.OrderBy(pack => pack.Manifest.Id, StringComparer.Ordinal)
            .ThenByDescending(pack => pack.Manifest.Version, Comparer<string>.Create(PackVersion.Compare)).ToArray();
    }
}
