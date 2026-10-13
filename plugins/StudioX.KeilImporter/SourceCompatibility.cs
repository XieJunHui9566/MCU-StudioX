namespace StudioX.KeilImporter;

using System.Text;

/// <summary>只转换已识别的旧 CMSIS 指令约束；字节替换保留原编码，修改仅发生在新工程副本。</summary>
public static class SourceCompatibility
{
    public static async Task<CompatibilityEdit[]> PlanAsync(CopyInput[] files, string[] applicationSources,
        List<ImportIssue> issues, CancellationToken token)
    {
        var edits = new List<CompatibilityEdit>();
        foreach (var file in files.Where(file => Path.GetFileName(file.Source).Equals("core_cm3.c", StringComparison.OrdinalIgnoreCase) &&
            applicationSources.Contains(file.RelativePath, StringComparer.OrdinalIgnoreCase)))
        {
            var bytes = await File.ReadAllBytesAsync(file.Source, token);
            var text = Encoding.Latin1.GetString(bytes);
            if (!text.Contains("V1.30", StringComparison.Ordinal) || !text.Contains("CMSIS", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var instruction in new[] { "strexb", "strexh", "strex" })
            {
                var before = "__ASM volatile (\"" + instruction + " %0, %2, [%1]\" : \"=r\" (result) : \"r\" (addr), \"r\" (value) );";
                var after = before.Replace("\"=r\"", "\"=&r\"", StringComparison.Ordinal);
                if (text.Contains(after, StringComparison.Ordinal))
                {
                    continue;
                }
                var first = text.IndexOf(before, StringComparison.Ordinal);
                if (first < 0 || text.IndexOf(before, first + before.Length, StringComparison.Ordinal) >= 0)
                {
                    issues.Add(new("error", "CMSIS_VARIANT", "旧 CMSIS V1.30 指令约束不是已验证写法，请先人工更新 GCC 约束：" + file.RelativePath + " / " + instruction));
                    continue;
                }
                edits.Add(new(file.RelativePath, before, after, "旧 CMSIS V1.30 的 STREX 输出必须与输入寄存器分离；为 GCC 增加 early-clobber 约束，仅修改新工程副本。"));
            }
        }
        if (edits.Count > 0)
        {
            issues.Add(new("warning", "CMSIS_GCC_FIX", "新工程副本将应用 " + edits.Count + " 处旧 CMSIS GCC 寄存器约束修正；原文件保持不变。请核对下方逐项源码修改预览。"));
        }
        return edits.ToArray();
    }

    public static async Task ApplyAsync(string copiedFile, CompatibilityEdit[] edits, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(copiedFile, token);
        // Latin1 对每个字节一一映射；ASCII 片段替换不转码中文注释或 CRLF。
        var text = Encoding.Latin1.GetString(bytes);
        foreach (var edit in edits)
        {
            var first = text.IndexOf(edit.Before, StringComparison.Ordinal);
            if (first < 0 || text.IndexOf(edit.Before, first + edit.Before.Length, StringComparison.Ordinal) >= 0)
            {
                throw new InvalidOperationException("副本中的兼容性修正不再唯一匹配，未发布新工程：" + edit.RelativePath);
            }
            text = text[..first] + edit.After + text[(first + edit.Before.Length)..];
        }
        await File.WriteAllBytesAsync(copiedFile, Encoding.Latin1.GetBytes(text), token);
    }
}
