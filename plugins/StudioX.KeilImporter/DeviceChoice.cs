namespace StudioX.KeilImporter;

using System.Text.Json;

public sealed record DeviceChoice(string PackId, string PackVersion, string ContentHash,
    string PackDirectory, JsonElement Device)
{
    public string Id => Device.GetProperty("id").GetString()!;
    public string Key => PackId + "/" + PackVersion + "/" + Id;
    public string Label => Id + " · " + PackId + " " + PackVersion;
}
