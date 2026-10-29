namespace StudioX.Engine;

public sealed record Mx251IspPreparation(string ProjectRoot, string SourceImage, string ImagePath, string ImageSha256,
    string ReceiptSha256, string LogPath, Mx251IspSettings Settings, int CodeBytes);
