namespace StudioX.Packages;

using StudioX.Foundation;

/// <summary>SDCC 数值选项按开关及参数整体叠加，模板可缩小内存边界。</summary>
internal static class SdccLinkOptions
{
    private static readonly HashSet<string> Paired = new(StringComparer.Ordinal)
    {
        "--code-loc", "--code-size", "--data-loc", "--idata-loc", "--iram-size",
        "--xram-loc", "--xram-size", "--xstack-loc", "--stack-loc", "--stack-size"
    };

    public static IReadOnlyList<string> Merge(IReadOnlyList<string> basis, IReadOnlyList<string> overlay)
    {
        var groups = new List<string[]>();
        foreach (var options in new[] { basis, overlay })
        {
            for (var index = 0; index < options.Count; index++)
            {
                var option = options[index];
                if (Paired.Contains(option))
                {
                    if (index + 1 == options.Count || options[index + 1].StartsWith('-'))
                    {
                        throw new StudioXException("PACK_BUILD_VALUE", "SDCC 选项缺少数值参数：" + option);
                    }
                    var group = new[] { option, options[++index] };
                    var previous = groups.FindIndex(value => value[0] == option);
                    if (previous >= 0)
                    {
                        groups[previous] = group;
                    }
                    else
                    {
                        groups.Add(group);
                    }
                }
                else if (!groups.Any(value => value.Length == 1 && value[0] == option))
                {
                    groups.Add([option]);
                }
            }
        }
        // 不对参数值做 Distinct；两个开关拥有相同的数值也是两个完整选项。
        return groups.SelectMany(value => value).ToArray();
    }
}
