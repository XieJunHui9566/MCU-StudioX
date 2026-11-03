using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

/// <summary>约束公共设计接入点，避免新弹窗绕过字体、主题和状态资源。</summary>
internal static class UiStyleChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = new DirectoryInfo(Environment.CurrentDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "UI_STYLE.md")))
        {
            root = root.Parent;
        }
        if (root is null)
        {
            throw new InvalidOperationException("Run UI style checks from the source repository.");
        }
        var desktop = Path.Combine(root.FullName, "src/StudioX.Desktop");
        check(File.ReadAllText(Path.Combine(root.FullName, "AGENTS.md")).Contains("UI_STYLE.md", StringComparison.Ordinal), "AGENTS points future UI work to the shared art specification");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var file in Directory.EnumerateFiles(desktop, "*.xaml"))
        {
            var page = XDocument.Load(file).Root!;
            if (page.Name.LocalName == "Window")
            {
                check((string?)page.Attribute("Style") == "{StaticResource {x:Type Window}}", Path.GetFileName(file) + " uses shared window typography");
                if (Path.GetFileName(file) != "MainWindow.xaml")
                {
                    check(!page.DescendantsAndSelf().Attributes("Foreground").Any(a => a.Value.StartsWith('#')), Path.GetFileName(file) + " uses semantic text colors");
                }
            }
            if ((string?)page.Attribute("FontFamily") is { } font && font.Contains("Segoe UI", StringComparison.Ordinal))
            {
                check(false, Path.GetFileName(file) + " must inherit workbench typography");
            }
            if ((string?)page.Attribute("FontFamily") == "{DynamicResource WorkbenchFontFamily}" && page.Attribute("FontSize") is { } size)
            {
                check(size.Value == "{DynamicResource WorkbenchFontSize}", Path.GetFileName(file) + " follows the user's UI font size");
            }
        }
        foreach (var file in Directory.EnumerateFiles(desktop, "*.cs"))
        {
            var code = File.ReadAllText(file);
            if (!file.EndsWith(".xaml.cs", StringComparison.Ordinal) && Regex.IsMatch(code, @"class\s+\w+\s*:\s*Window\b"))
            {
                check(code.Contains("SetResourceReference(StyleProperty, typeof(Window))", StringComparison.Ordinal), Path.GetFileName(file) + " uses the common window style");
            }
        }
        var app = XDocument.Load(Path.Combine(desktop, "App.xaml"));
        var primary = app.Descendants().Single(e => (string?)e.Attribute(xaml + "Key") == "PrimaryButton");
        foreach (var (property, key) in new[] { ("Background", "Accent"), ("BorderBrush", "Accent"), ("Foreground", "OnAccent") })
        {
            check(primary.Elements().Any(e => (string?)e.Attribute("Property") == property && (string?)e.Attribute("Value") == "{DynamicResource " + key + "}"), "Primary button " + property + " follows custom themes");
        }
        check(!File.ReadAllText(Path.Combine(desktop, "AiTranscriptRenderer.cs")).Contains("Brushes.White", StringComparison.Ordinal), "AI bubbles follow OnAccent rather than assuming white text");
    }
}
