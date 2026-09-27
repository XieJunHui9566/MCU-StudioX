namespace StudioX.Engine;

public sealed record StcIspPreview(string ProjectRoot, string ExpectedModel, uint ExpectedCodeBytes,
    string Port, string SourceImage, string ImageSha256, int ImageBytes, int DataBytes,
    int HighestAddress, StcIspSettings Settings, StcIspToolStatus Tool);
