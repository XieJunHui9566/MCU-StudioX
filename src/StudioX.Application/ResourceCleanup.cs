namespace StudioX.Application;

/// <summary>按既定顺序尝试释放所有资源，最后汇总原始异常；一个失败不能跳过其他所有者。</summary>
internal sealed class ResourceCleanup
{
    private readonly List<Exception> errors = [];

    public void Record(Exception error) => errors.Add(error);

    public void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    public async ValueTask RunAsync(Func<ValueTask> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception error)
        {
            errors.Add(error);
        }
    }

    public void ThrowIfFailed(string message)
    {
        if (errors.Count > 0)
        {
            throw new AggregateException(message, errors);
        }
    }
}
