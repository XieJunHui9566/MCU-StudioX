namespace StudioX.PluginValidation;

using StudioX.Foundation;

/// <summary>行为门禁收集器；失败立即保留原始诊断并阻断交付。</summary>
internal sealed class ValidationChecks
{
    public List<string> Results { get; } = [];

    public void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidOperationException("验证失败：" + description);
        }
        Results.Add(description);
        Console.WriteLine("PASS " + description);
    }

    public async Task RejectAsync(Func<Task> action, string description, params string[] codes)
    {
        try
        {
            await action();
        }
        catch (StudioXException exception) when (codes.Length == 0 || codes.Contains(exception.Code, StringComparer.Ordinal))
        {
            Check(true, description);
            return;
        }
        throw new InvalidOperationException("边界未拒绝：" + description);
    }
}
