namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record KeilTarget(string Name, string Device, string Compiler, string[] Sources,
    string[] IncludeDirectories, string[] Defines, string[] Excluded, ImportIssue[] Issues,
    KeilMemory[] Memory, string[] SupportFiles);

public sealed record KeilMemory(string Name, uint Origin, uint Bytes);

public sealed record ImportIssue(string Severity, string Code, string Message);

public sealed record DeviceChoice(string PackId, string PackVersion, string ContentHash,
    string PackDirectory, JsonElement Device)
{
    public string Id => Device.GetProperty("id").GetString()!;
    public string Key => PackId + "/" + PackVersion + "/" + Id;
    public string Label => Id + " · " + PackId + " " + PackVersion;
}

public sealed record ImportRequest(string ProjectFile, string SourceRoot, string TargetName,
    string ProjectName, string ParentDirectory, string CliPath, string PackRepository,
    DeviceChoice Device, string TemplateId);

public sealed record CopyInput(string Source, string RelativePath, long Bytes, string Sha256);

public sealed record CompatibilityEdit(string RelativePath, string Before, string After, string Reason);

public sealed record ImportPreview(ImportRequest Request, string Destination, KeilTarget Target,
    CopyInput[] Files, string[] ApplicationSources, string[] IncludeDirectories,
    string[] PackSources, string[] AdditionalDefines, CompatibilityEdit[] Edits, ImportIssue[] Issues, string CMake,
    string CliHash, string PreviewId)
{
    public bool CanCreate => Issues.All(issue => issue.Severity != "error");
}

public sealed record CreationResult(string Directory, string DeviceId, int CopiedFiles, string PreviewId,
    bool CompilationVerified, string BuildLog, string? BuildError);
