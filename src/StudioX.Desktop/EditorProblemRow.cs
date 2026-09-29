namespace StudioX.Desktop;

using StudioX.Application;

internal sealed record EditorProblemRow(BuildDiagnostic Diagnostic, string Origin, string Snapshot, int? Offset = null, int Length = 1)
{
    public string Severity => Diagnostic.IsWarning ? "警告 Warning" : "错误 Error";
    public string File => Diagnostic.RelativePath;
    public int Line => Diagnostic.Line;
    public int Column => Diagnostic.Column;
    public string Message => Diagnostic.Message;
    public string ChineseMessage => DiagnosticText.ChineseSummary(Message);
    public string BilingualMessage => DiagnosticText.Bilingual(Message);
}
