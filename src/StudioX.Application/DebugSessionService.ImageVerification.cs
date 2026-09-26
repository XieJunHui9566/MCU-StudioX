namespace StudioX.Application;

using System.Text;
using StudioX.Engine;
using StudioX.Engine.Debugging;
using StudioX.Foundation;

public sealed partial class DebugSessionService
{
    private static async Task VerifyImageAsync(GdbDebugAdapter adapter, string command, HardwareDebugPreparation preparation, CancellationToken token)
    {
        var output = new StringBuilder();
        var sync = new object();
        var truncated = false;
        void Observe(string text)
        {
            var record = MiRecord.Parse(text);
            // 只收实际工具的流输出，不从发送的命令、文件名或旧日志寻找成功字样。
            if (record.Kind is not ('@' or '&' or '~') || record.Data.Text is not { } line) return;
            lock (sync)
            {
                if (output.Length + line.Length > 1024 * 1024) { truncated = true; return; }
                output.Append(line);
                if (!line.EndsWith('\n')) output.AppendLine();
            }
        }
        adapter.RecordReceived += Observe;
        try
        {
            try { await adapter.SendAsync(command, token); }
            catch (StudioXException ex) when (ex.Code == "GDB_COMMAND")
            { throw ExplainImageVerificationFailure(ex, preparation.LogPath); }
            lock (sync)
                RequireImageVerificationEvidence(output.ToString(), preparation.ImageByteCount, preparation.LogPath, truncated);
        }
        finally { adapter.RecordReceived -= Observe; }
    }

    internal static void RequireImageVerificationEvidence(string output, ulong expectedBytes, string logPath, bool truncated = false)
        => FirmwareVerificationEvidence.Require(output, expectedBytes, logPath, truncated);
}
