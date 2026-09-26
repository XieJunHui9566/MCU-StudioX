using StudioX.Application;
using StudioX.Foundation;

internal static class ImageVerificationChecks
{
    public static async Task<int> RunAsync(string output)
    {
        var root = Path.GetFullPath(output);
        if (Directory.Exists(root)) throw new ArgumentException("Use a new validation directory.");
        Directory.CreateDirectory(root);
        var results = new List<string>();
        void Pass(string text) { results.Add(text); Console.WriteLine("PASS " + text); }
        void Accept(string text, ulong expected, string label)
        {
            DebugSessionService.RequireImageVerificationEvidence(text, expected, "fixture.log");
            Pass(label);
        }
        void Reject(string text, ulong expected, string code, string label, bool truncated = false)
        {
            try { DebugSessionService.RequireImageVerificationEvidence(text, expected, "fixture.log", truncated); }
            catch (StudioXException ex) when (ex.Code == code && ex.Message.Contains("fixture.log", StringComparison.Ordinal))
            { Pass(label); return; }
            throw new InvalidOperationException("Expected " + code + ": " + label);
        }
        Accept("verified 355780 bytes in 3.247349s (106.992 KiB/s)\n", 355780, "F407 actual console success verifies all ELF load bytes");
        Accept("OpenOCD: Info : verified 5628 bytes in 0.3s\r\nverified 5628 bytes in 0.3s\n", 5628,
            "WCH/OpenOCD and GDB duplicated matching verification output accepted");
        Reject("checksum mismatch - attempting binary compare\ndiff 0 address 0x00000001. Was 0x00 instead of 0x10\n13^done\n", 5628,
            "DEBUG_IMAGE_MISMATCH", "WCH real byte differences rejected even when MI returns done");
        Reject("diff 0 address 0x00000001. Was 0x00 instead of 0x10\nerror reading USB data\n", 5628,
            "DEBUG_IMAGE_VERIFY_TRANSPORT", "USB failure takes precedence over apparent differences");
        Reject("13^done\n", 5628, "DEBUG_IMAGE_VERIFY", "MI done alone does not prove image identity");
        Reject("verified 1548 bytes in 0.3s\n", 5628, "DEBUG_IMAGE_VERIFY", "Incomplete or wrong image byte count rejected");
        Reject("verified 5628 bytes\n", 0, "DEBUG_IMAGE_VERIFY", "Missing expected load size rejected");
        Reject("verified 5628 bytes\n", 5628, "DEBUG_IMAGE_VERIFY", "Truncated verification output rejected", true);
        Reject("verified 5628 bytes\ndiff 1 address 0x00000003. Was 0x25 instead of 0x53\n", 5628,
            "DEBUG_IMAGE_MISMATCH", "Positive text cannot override actual byte differences");
        Reject("START echo verified 5628 bytes\n", 5628, "DEBUG_IMAGE_VERIFY", "Command text is not a verification success line");
        Reject("verified 5628 bytes\nverified 1548 bytes\n", 5628, "DEBUG_IMAGE_VERIFY", "Conflicting verification counts rejected");
        Accept("checksum mismatch - attempting binary compare\nverified 5628 bytes in 0.3s\n", 5628,
            "Successful complete binary comparison remains valid after CRC fallback");
        await File.WriteAllLinesAsync(Path.Combine(root, "result.txt"), results.Prepend("PASS — offline verification-output regressions; no target connected"));
        return 0;
    }
}
