namespace StudioX.Application.Tools;

using StudioX.Engine;

public sealed record DevelopmentComponentInstallResult(DevelopmentComponentIdentity Identity, string Fingerprint, bool AlreadyInstalled);
