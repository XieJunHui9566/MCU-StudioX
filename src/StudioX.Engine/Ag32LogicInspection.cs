namespace StudioX.Engine;

public sealed record Ag32LogicInspection(string ProjectDirectory, Ag32LogicProjectSettings Settings,
    string VerilogPath, string PinMapPath, string BinaryPath, int AssignedPinCount, long? BinaryBytes,
    string? QuartusExecutable, string? SupraExecutable, IReadOnlyList<string> Errors, IReadOnlyList<string> Notes);
