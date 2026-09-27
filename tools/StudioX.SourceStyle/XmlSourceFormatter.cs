namespace StudioX.SourceStyle;

using System.Text;
using System.Xml;
using System.Xml.Linq;

/// <summary>展开 XAML 和构建 XML 的结构；保留混合文本、命名空间和显式 xml:space 内容。</summary>
internal static class XmlSourceFormatter
{
    public static string Format(string text)
    {
        var document = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        foreach (var element in document.Descendants())
        {
            if (!element.HasElements || element.AncestorsAndSelf().Any(ancestor =>
                (string?)ancestor.Attribute(XNamespace.Xml + "space") == "preserve"))
            {
                continue;
            }
            var texts = element.Nodes().OfType<XText>().ToArray();
            if (texts.Any(value => !string.IsNullOrWhiteSpace(value.Value)))
            {
                continue;
            }
            foreach (var value in texts)
            {
                value.Remove();
            }
        }
        foreach (var value in document.Nodes().OfType<XText>().Where(value => string.IsNullOrWhiteSpace(value.Value)).ToArray())
        {
            value.Remove();
        }
        var builder = new StringBuilder();
        using (var textWriter = new Utf8StringWriter(builder))
        {
            using (var writer = XmlWriter.Create(textWriter, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                NewLineChars = "\n",
                NewLineHandling = NewLineHandling.Entitize,
                OmitXmlDeclaration = document.Declaration is null,
                NewLineOnAttributes = true
            }))
            {
                document.WriteTo(writer);
            }
        }
        var result = builder.ToString().TrimEnd() + "\n";
        // 只比较 XML 数据；换行和属性分行不能修改绑定、资源键或可见文本。
        var before = XDocument.Parse(text);
        var after = XDocument.Parse(result);
        if (!XNode.DeepEquals(before, after))
        {
            throw new InvalidOperationException("XML 排版改变了内容，已停止写入。");
        }
        return result;
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder)
    {
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
