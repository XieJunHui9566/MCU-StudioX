namespace StudioX.Engine.Arduino;

using StudioX.Foundation;

/// <summary>工程锁定 Arduino 板型、核心、CLI 和包括传递依赖在内的库版本。</summary>
public sealed record ArduinoProjectSettings(string BoardId, ArduinoLibrary[] Libraries)
{
    public const string ToolsetId = "arduino.avr";
    public const string CoreVersion = "1.8.8";
    public const string CliVersion = "1.5.1";
    public const string CompilerId = "avr-gcc-7.3.0-arduino7";
    public ArduinoPlatform Platform => ArduinoBoard.Require(BoardId).Platform;

    public void Validate()
    {
        _ = ArduinoBoard.Require(BoardId);
        if (Libraries is null || Libraries.Length > 128 || Libraries.Any(item => item is null) ||
            Libraries.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != Libraries.Length)
        {
            throw new StudioXException("ARDUINO_LIBRARIES", "Arduino 库清单为空、重复或超出 128 项限制。");
        }
        foreach (var library in Libraries)
        {
            library.Validate();
        }
    }

    public bool Equals(ArduinoProjectSettings? other) => other is not null && BoardId == other.BoardId &&
        Libraries.SequenceEqual(other.Libraries);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(BoardId);
        foreach (var library in Libraries)
        {
            hash.Add(library);
        }
        return hash.ToHashCode();
    }

    public static void ValidateProject(string root, ProjectManifest project)
    {
        project.Arduino?.Validate();
        var board = project.Arduino is null ? null : ArduinoBoard.Require(project.Arduino.BoardId);
        var platform = board?.Platform;
        if (project.Kind != ProjectKind.Arduino || project.Arduino is null || project.ToolsetId != platform!.ToolsetId ||
            project.ToolsetVersion != platform.CoreVersion || project.CompilerId != platform.CompilerId || project.CubeMx is not null ||
            project.Espressif is not null || project.MicroPython is not null || project.Zephyr is not null ||
            project.Logic is not null || project.PinMapping is not null ||
            (project.DeviceId != board!.Fqbn && project.DeviceId != board.DeviceId) ||
            project.EntryFile != project.Name + ".ino" || Path.GetFileName(Path.GetFullPath(root)) != project.Name)
        {
            throw new StudioXException("ARDUINO_PROJECT", "Arduino 工程身份、目录名、入口或工具版本不一致。");
        }
        _ = PathBoundary.Resolve(root, project.EntryFile);
    }
}
