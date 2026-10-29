using StudioX.Application;
using StudioX.Foundation;
using StudioX.Packages;

internal static class McuFavoritesChecks
{
    internal static async Task RunAsync(string output, Action<string> pass)
    {
        var data = Path.Combine(output, "mcu-favorites");
        var service = new McuFavoritesService(data);
        void Check(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
            pass("PASS: MCU favorites " + message);
        }
        DeviceDefinition Device(string id) => new(id, id, "arm", 0x08000000, 131072, 0x20000000, 20480,
            "test.toolset", "1.0.0", "test-gcc", [], [], [], [], "link.ld", [], [],
            [new ProjectTemplate("bare", "Bare", "Test", "main.c")]);
        InstalledPack Pack(string id, string version, params DeviceDefinition[] devices) =>
            new(new(1, id, version, id, "Test", devices), data, "fixture");
        var first = Device("same-model");
        var second = Device("other-model");
        var original = Pack("test.first", "1.0.0", first, second);
        var other = Pack("test.second", "1.0.0", first);
        Check((await service.LoadAsync()).Count == 0 && !Directory.Exists(data), "empty read does not create preferences");
        await Task.WhenAll(service.AddAsync(original, first), service.AddAsync(original, first), service.AddAsync(other, first));
        Check((await new McuFavoritesService(data).LoadAsync()).Count == 2, "concurrent adds deduplicate exact identity and survive reload");
        var latest = Pack("test.first", "1.10.0", first, second);
        var previous = Pack("test.first", "1.9.0", first);
        var options = await service.OptionsAsync([original, latest, previous, other]);
        Check(options.Single(item => item.Favorite.PackId == "test.first").Pack == latest, "upgrade resolves newest numeric version without pinning preferences");
        var removedModel = Pack("test.first", "2.0.0", second);
        options = await service.OptionsAsync([original, removedModel, other]);
        Check(options.Single(item => item.Favorite.PackId == "test.first").Pack == original, "new package without model does not conceal an installed exact model");
        options = await service.OptionsAsync([removedModel, other]);
        var missing = options.Single(item => item.Favorite.PackId == "test.first");
        Check(!missing.IsAvailable && missing.Device is null && missing.Label.Contains("未安装"), "missing model stays visible without substitution from another package");
        await service.RemoveAsync(missing.Favorite);
        Check((await service.LoadAsync()).Single().PackId == "test.second", "unavailable favorite can be removed independently");
        try
        {
            await service.AddAsync(other, second);
            throw new InvalidOperationException("Foreign model accepted.");
        }
        catch (StudioXException ex) when (ex.Code == "MCU_FAVORITE_DEVICE") { pass("PASS: MCU favorites reject foreign device"); }
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            try
            {
                await service.AddAsync(original, second, cancellation.Token);
                throw new InvalidOperationException("Cancelled mutation accepted.");
            }
            catch (OperationCanceledException) { }
        }
        Check((await service.LoadAsync()).Count == 1, "cancelled mutation preserves stored preferences");
        var path = Path.Combine(data, "mcu-favorites.json");
        foreach (var invalid in new[] { "{", "{\"formatVersion\":2,\"items\":[]}", "{\"formatVersion\":1,\"items\":[null]}" })
        {
            await File.WriteAllTextAsync(path, invalid);
            try
            {
                await service.AddAsync(original, first);
                throw new InvalidOperationException("Invalid preferences overwritten.");
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or StudioXException) { }
            Check(await File.ReadAllTextAsync(path) == invalid, "invalid or future preferences are reported and preserved");
        }
    }
}
