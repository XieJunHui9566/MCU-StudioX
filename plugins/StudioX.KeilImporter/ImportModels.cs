namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record KeilTarget(string Name, string Device, string Compiler, string[] Sources,
    string[] IncludeDirectories, string[] Defines, string[] Excluded, ImportIssue[] Issues,
    KeilMemory[] Memory, string[] SupportFiles);
