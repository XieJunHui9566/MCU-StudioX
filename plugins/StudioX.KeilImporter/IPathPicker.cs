namespace StudioX.KeilImporter;

/// <summary>选择窗口仅返回路径；复制与编译仍必须经过原来的预览确认。</summary>
public interface IPathPicker
{
    Task<string?> PickAsync(PathPickerRequest request, CancellationToken token);
}
