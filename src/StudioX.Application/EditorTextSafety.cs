namespace StudioX.Application;

using System.Globalization;
using StudioX.Foundation;

/// <summary>在正文进入编辑器前限制排版复杂度，避免长行假死及异常 Unicode 簇触发本机文字引擎故障。</summary>
public static class EditorTextSafety
{
    public const int MaximumLineLength = 64 * 1024;
    public const int MaximumTextElementLength = 256;

    public static void Validate(string text, CancellationToken token = default)
    {
        if (text.Contains('\0'))
        {
            throw new StudioXException("EDITOR_BINARY", "此文件是二进制文件，无法作为源代码打开。");
        }
        var offset = 0;
        var lineLength = 0;
        while (offset < text.Length)
        {
            if ((offset & 4095) == 0) { token.ThrowIfCancellationRequested(); }
            if (text[offset] is '\r' or '\n')
            {
                offset++;
                lineLength = 0;
                continue;
            }
            // ASCII 后接组合字符时也必须从基字符计算整个簇；普通源码无需逐字符分配字符串。
            var length = text[offset] <= 0x7f && (offset + 1 == text.Length || text[offset + 1] <= 0x7f)
                ? 1 : StringInfo.GetNextTextElementLength(text.AsSpan(offset));
            if (length > MaximumTextElementLength)
            {
                throw new StudioXException("EDITOR_TEXT_COMPLEXITY", "文件包含异常过长的 Unicode 组合字符，无法安全显示；原文件和已有草稿未修改。");
            }
            lineLength += length;
            if (lineLength > MaximumLineLength)
            {
                throw new StudioXException("EDITOR_LINE_LENGTH", "文件单行长度超过 65,536 个字符，无法安全编辑；请使用适合大型文本的工具分行后再打开。原文件和已有草稿未修改。");
            }
            offset += length;
        }
        token.ThrowIfCancellationRequested();
    }
}
