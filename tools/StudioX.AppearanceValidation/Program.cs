using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using StudioX.AppearancePlugin;
using StudioX.Application;
using StudioX.Application.Plugins;
using StudioX.Extensions;
using StudioX.Foundation;

if (args is not [var output])
{
    throw new ArgumentException("Usage: <new-output>");
}
output = Path.GetFullPath(output);
if (Directory.Exists(output))
{
    throw new ArgumentException("New output required.");
}
Directory.CreateDirectory(output);
var checks = new List<string>();
void Check(bool condition, string name)
{
    if (!condition)
    {
        throw new InvalidOperationException(name);
    }
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
void Reject(Action action, string name)
{
    try
    {
        action();
    }
    catch (Exception error) when (error is StudioXException or JsonException)
    {
        Check(true, name);
        return;
    }
    throw new InvalidOperationException(name);
}
var plugin = new AppearanceStudioPlugin();
var contribution = plugin.Describe();
var widget = contribution.Panels.Single().Widgets.Single();
var manifest = new PluginManifest(1, 3, "studiox.appearance-studio", "1.1.0", "外观工作室", "plugin.dll", "Plugin",
    ["panels"], [])
{
    Scope = "application"
};
PluginContributionValidator.Validate(manifest, contribution);
var catalog = PluginAppearanceCatalog.Parse(widget.Value!.Value);
Check(catalog.Themes.Length == 9 && catalog.Themes.Count(theme => theme.Artwork is not null) == 3 &&
    contribution.Commands.Length == 0 && contribution.AgentTools.Length == 0, "six palettes and three environment themes without host commands");
var legacy = catalog with { FormatVersion = 1, Themes = catalog.Themes.Take(6).ToArray() };
Check(PluginAppearanceCatalog.Parse(JsonSerializer.SerializeToElement(legacy, JsonStore.Options)).Themes.Length == 6,
    "legacy palette-only format 1 remains supported");
Reject(() => PluginContributionValidator.Validate(manifest with { Scope = "project" }, contribution), "project plugins cannot change application appearance");
Reject(() => PluginContributionValidator.ValidatePanel(contribution.Panels[0] with
{
    Widgets = [widget with { CommandId = "apply" }]
}, new HashSet<string> { "apply" }, true), "appearance widget cannot invoke plugin command");
var original = JsonSerializer.SerializeToNode(catalog, JsonStore.Options)!.AsObject();
void RejectCatalog(Action<JsonObject> edit, string name)
{
    var modified = (JsonObject)original.DeepClone();
    edit(modified);
    Reject(() => PluginAppearanceCatalog.Parse(JsonSerializer.SerializeToElement(modified)), name);
}
RejectCatalog(data => data["formatVersion"] = 3, "unknown appearance format rejected");
RejectCatalog(data => data["formatVersion"] = 1, "artwork requires explicit format 2");
RejectCatalog(data => data["themes"]![6]!["artwork"]!["asset"] = "../hina.png", "artwork path traversal rejected");
RejectCatalog(data => data["themes"]![6]!["artwork"]!["asset"] = "https://host/hina.png", "remote artwork rejected");
RejectCatalog(data => data["themes"]![6]!["artwork"]!["asset"] = "assets/view.xaml", "executable artwork format rejected");
RejectCatalog(data => data["themes"]![6]!["artwork"]!["sha256"] = "missing", "unverified artwork rejected");
RejectCatalog(data => data["themes"]![6]!["artwork"]!["opacity"] = 1.1, "excessive artwork intensity rejected");
RejectCatalog(data => data["background"] = "video.mp4", "media fields rejected");
RejectCatalog(data => data["fontPath"] = "file:///font.ttf", "font resources rejected");
RejectCatalog(data => data["themes"] = new JsonArray(), "empty theme catalog rejected");
RejectCatalog(data => data["themes"]![0]!["id"] = "studiox.dark", "built-in IDs cannot be overwritten");
RejectCatalog(data => data["themes"]![1]!["id"] = data["themes"]![0]!["id"]!.DeepClone(), "duplicate theme IDs rejected");
RejectCatalog(data => data["themes"]![0]!["colors"]!["Accent"] = "red", "non-hex colors rejected");
RejectCatalog(data => data["themes"]![0]!["colors"]!["Extra"] = "#FFFFFF", "unknown semantic colors rejected");
RejectCatalog(data => data["themes"]![0]!["colors"]!.AsObject().Remove("Text"), "missing semantic colors rejected");
RejectCatalog(data => data["themes"]![0]!["xaml"] = "<Window/>", "executable markup rejected");
Reject(() => TypographyService.Validate(new(FontSize: double.NaN)), "nonfinite interface size rejected");
Reject(() => TypographyService.Validate(new(FontSize: 30)), "oversize interface font rejected");
Reject(() => EditorSettingsService.Validate(new(FontFamily: "https://host/font.ttf#Font")), "editor font URI rejected");
Reject(() => TypographyService.Validate(new(FontFamily: "C:\\font.ttf#Font")), "interface font path rejected");
Reject(() => TypographyService.Validate(new(FontFamily: "Segoe UI\n")), "font control characters rejected");

var dataDirectory = Path.Combine(output, "data");
var themes = new ThemeService(dataDirectory);
var editor = new EditorSettingsService(dataDirectory);
var typography = new TypographyService(dataDirectory);
var appearance = new AppearanceService(dataDirectory);
var styles = new WorkbenchStyleService(themes, typography, editor);
var artworkDefinition = catalog.Themes.First(theme => theme.Artwork is not null).Artwork!;
var fixture = Path.Combine(AppContext.BaseDirectory, "theme-fixtures", Path.GetFileName(artworkDefinition.Asset));
var artworkBytes = await ThemeArtworkService.ReadVerifiedAsync(fixture, artworkDefinition.Sha256, CancellationToken.None);
var artwork = ThemeArtworkService.FromPlugin(artworkDefinition);
await themes.Artwork.ImportAsync(artwork, artworkBytes);
Check(artworkBytes.SequenceEqual(themes.Artwork.Read(artwork)), "verified environment PNG persists independently from wallpaper");
var damaged = (byte[])artworkBytes.Clone();
damaged[^1] ^= 1;
try
{
    await themes.Artwork.ImportAsync(artwork, damaged);
    throw new InvalidOperationException("damaged artwork accepted");
}
catch (StudioXException error) when (error.Code == "THEME_ARTWORK_HASH")
{
    Check(true, "modified PNG cannot replace a verified cached image");
}
var oversized = (byte[])artworkBytes.Clone();
System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(oversized.AsSpan(16, 4), 10000);
var oversizedName = Convert.ToHexString(SHA256.HashData(oversized)).ToLowerInvariant() + ".png";
try
{
    await themes.Artwork.ImportAsync(new(oversizedName), oversized);
    throw new InvalidOperationException("oversized artwork accepted");
}
catch (StudioXException error) when (error.Code == "THEME_ARTWORK_FORMAT")
{
    Check(true, "oversized decoded image dimensions rejected before persistence");
}
Reject(() => ThemeArtworkService.Validate(new("../outside.png")), "cached artwork paths cannot escape user theme data");
Reject(() => ThemeArtworkService.Validate(new(artwork.Asset, double.NaN)), "nonfinite artwork intensity rejected");
var fakeVideo = Path.Combine(output, "fixture.mp4");
await File.WriteAllBytesAsync(fakeVideo, [1, 2, 3, 4]);
var asset = await appearance.ImportAsync(fakeVideo, BackgroundKind.Video);
var background = new BackgroundSettings(1, BackgroundKind.Video, asset, .6, .2, 3, BackgroundFit.Fit, false, false);
await appearance.SaveAsync(background);
var originalMedia = await File.ReadAllBytesAsync(Path.Combine(dataDirectory, "appearance.json"));
var originalAsset = await File.ReadAllBytesAsync(appearance.Resolve(background)!);
Check(await themes.LoadAsync() == ThemeService.Dark || (await themes.LoadAsync()).Id == "studiox.dark", "legacy users retain default dark theme");
Check((await typography.LoadAsync()).FontSize == 13, "missing typography preferences keep legacy default");
foreach (var preset in catalog.Themes)
{
    var theme = PluginAppearanceCatalog.ToTheme(preset);
    await styles.SaveAsync(new(theme, new(FontFamily: "Microsoft YaHei UI", FontSize: 15),
        new(FontFamily: "Consolas", FontSize: 18, SoftGlow: false, GlowStrength: 42)
        {
            Bold = true,
            Italic = true
        }));
    var reloaded = await new ThemeService(dataDirectory).LoadAsync();
    Check(reloaded.Id == preset.Id && reloaded.Artwork == theme.Artwork, "restart retains palette and artwork " + preset.Id);
}
var savedEditor = await new EditorSettingsService(dataDirectory).LoadAsync();
Check(savedEditor.Bold && savedEditor.Italic && savedEditor.FontSize == 18 && !savedEditor.SoftGlow && savedEditor.GlowStrength == 42,
    "font style and unrelated editor settings survive reload");
Check((await new TypographyService(dataDirectory).LoadAsync()).FontFamily == "Microsoft YaHei UI", "interface font persists independently");
var savedMedia = await File.ReadAllBytesAsync(Path.Combine(dataDirectory, "appearance.json"));
var savedAsset = await File.ReadAllBytesAsync(appearance.Resolve(background)!);
Check(originalMedia.SequenceEqual(savedMedia) && originalAsset.SequenceEqual(savedAsset), "wallpaper settings and asset remain byte identical");
var cancelled = new CancellationToken(true);
try
{
    await styles.SaveAsync(new(ThemeService.Dark, new(), new()), cancelled);
    throw new InvalidOperationException("cancelled save accepted");
}
catch (OperationCanceledException)
{
    Check((await themes.LoadAsync()).Id == catalog.Themes[^1].Id, "cancelled operation does not change preferences");
}
// 目录占位使第二步的原子文件发布失败，核对先写入的字体偏好会恢复。
var blockedData = Path.Combine(output, "blocked-data");
Directory.CreateDirectory(Path.Combine(blockedData, "editor.json"));
var blockedTypography = new TypographyService(blockedData);
await blockedTypography.SaveAsync(new(FontSize: 14));
var blockedStyles = new WorkbenchStyleService(new(blockedData), blockedTypography, new(blockedData));
try
{
    await blockedStyles.SaveAsync(new(ThemeService.Light, new(FontSize: 16), new()));
    throw new InvalidOperationException("blocked save accepted");
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException)
{
    Check((await blockedTypography.LoadAsync()).FontSize == 14 && !File.Exists(Path.Combine(blockedData, "preferences.json")),
        "failed save restores fonts without changing theme");
}
if (OperatingSystem.IsWindows())
{
    var previousTheme = await themes.LoadAsync();
    var modifiedTheme = previousTheme with
    {
        Colors = new(previousTheme.Colors)
        {
            ["Accent"] = "#123456"
        }
    };
    using var lockedPreferences = new FileStream(Path.Combine(dataDirectory, "preferences.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
    try
    {
        await styles.SaveAsync(new(modifiedTheme, new(FontSize: 16), new(FontSize: 20)));
        throw new InvalidOperationException("locked theme selection accepted");
    }
    catch (AggregateException error)
    {
        Check(error.InnerExceptions.Count == 2 && (await themes.LoadAsync()).Colors["Accent"] == previousTheme.Colors["Accent"] &&
            await editor.LoadAsync() == savedEditor && (await typography.LoadAsync()).FontSize == 15,
            "partial theme write restores colors and fonts and preserves original rollback errors");
    }
}
await JsonStore.WriteAsync(Path.Combine(output, "result.json"), new { passed = true, hardware = false, mediaPlaybackTested = false, checks });
