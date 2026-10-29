/// <summary>并发验收使用同步屏障拦截准备阶段，避免独占租约探测与实际共享获取互相干扰。</summary>
internal sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
