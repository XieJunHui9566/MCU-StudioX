namespace StudioX.PicValidation;

internal sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
