namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record ImportRequest(string ProjectFile, string SourceRoot, string TargetName,
    string ProjectName, string ParentDirectory, string CliPath, string PackRepository,
    DeviceChoice Device, string TemplateId);
