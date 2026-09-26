namespace StudioX.Packages;

/// <summary>格式 1 的三段整数版本按数值排序，允许超过 Int32 的版本段。</summary>
public static class PackVersion
{
    public static int Compare(string left, string right)
    {
        PackValidator.Version(left); PackValidator.Version(right);
        var a = left.Split('.'); var b = right.Split('.');
        for (var i = 0; i < 3; i++)
        {
            var result = a[i].Length.CompareTo(b[i].Length);
            if (result == 0) result = string.CompareOrdinal(a[i], b[i]);
            if (result != 0) return result;
        }
        return 0;
    }
}
