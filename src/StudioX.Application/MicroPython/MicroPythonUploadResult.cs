namespace StudioX.Application.MicroPython;

public sealed record MicroPythonUploadResult(string Path, int Bytes, string Sha256, string? BackupPath);
