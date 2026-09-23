namespace StudioX.Foundation;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut, bool OutputTruncated)
{
    public bool Success => !TimedOut && ExitCode == 0;
}
