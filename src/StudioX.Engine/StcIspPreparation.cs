namespace StudioX.Engine;

public sealed record StcIspPreparation(string ProjectRoot, string ExpectedModel, uint ExpectedCodeBytes, string Port, string SourceImage, string Image,
    string ImageSha256, int DataBytes, int HighestAddress, string PythonExecutable, string GuardScript, string LogPath,
    StcIspSettings Settings);
