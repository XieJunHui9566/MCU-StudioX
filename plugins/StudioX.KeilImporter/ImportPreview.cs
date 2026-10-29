namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record ImportPreview(ImportRequest Request, string Destination, KeilTarget Target,
    CopyInput[] Files, string[] ApplicationSources, string[] IncludeDirectories,
    string[] PackSources, string[] AdditionalDefines, CompatibilityEdit[] Edits, ImportIssue[] Issues, string CMake,
    string CliHash, string PreviewId)
{
    public bool CanCreate => Issues.All(issue => issue.Severity != "error");
}
