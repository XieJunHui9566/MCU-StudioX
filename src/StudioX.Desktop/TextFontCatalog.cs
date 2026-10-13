namespace StudioX.Desktop;

using System.Windows;
using System.Windows.Media;
using StudioX.Application;
using StudioX.Foundation;

/// <summary>以实际字形覆盖筛选文本字体，防止符号字体把工程名和代码渲染成图形。</summary>
internal static class TextFontCatalog
{
    private const string RequiredCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.-/";
    private static readonly Lazy<Catalog> installed = new(ReadInstalled);

    internal static IReadOnlyList<string> Families => installed.Value.Families;
    internal static IReadOnlyList<string> Diagnostics => installed.Value.Diagnostics;

    internal static bool IsTextFamily(string family) => !string.IsNullOrWhiteSpace(family) &&
        family.Split(',').All(part => installed.Value.Names.Contains(part.Trim()));

    internal static void Validate(string family)
    {
        TypographyService.ValidateFontFamily(family);
        if (!IsTextFamily(family))
        {
            throw new StudioXException("TYPOGRAPHY_TEXT_FONT", "请选择能正常显示字母、数字和文件名的已安装文本字体，不能使用符号或图标字体：" + family);
        }
    }

    internal static TypographySettings Recover(TypographySettings settings) => IsTextFamily(settings.FontFamily)
        ? settings : settings with
        {
            FontFamily = new TypographySettings().FontFamily
        };

    internal static EditorSettings Recover(EditorSettings settings) => IsTextFamily(settings.FontFamily)
        ? settings : settings with
        {
            FontFamily = "Consolas"
        };

    private static Catalog ReadInstalled()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies)
        {
            try
            {
                if (family.Source.Length > 100)
                {
                    continue;
                }
                TypographyService.ValidateFontFamily(family.Source);
                var face = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                // 部分工程符号字体未标记 Symbol，仍需逐字检查常见文件名字符。
                if (face.TryGetGlyphTypeface(out var glyph) && !glyph.Symbol &&
                    RequiredCharacters.All(character => glyph.CharacterToGlyphMap.TryGetValue(character, out var index) && index != 0))
                {
                    names.Add(family.Source);
                }
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.IO.IOException or System.IO.FileFormatException or StudioXException)
            {
                diagnostics.Add("字体元数据读取失败：" + family.Source + "\n" + error);
            }
        }
        return new(names, names.Order(StringComparer.CurrentCultureIgnoreCase).ToArray(), diagnostics.ToArray());
    }

    private sealed record Catalog(HashSet<string> Names, string[] Families, string[] Diagnostics);
}
