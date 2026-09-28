namespace StudioX.Application.Plugins;

/// <summary>由桌面提供实时编辑缓冲区与定位；应用层不依赖具体编辑器控件。</summary>
public interface IPluginEditorAccess
{
    Task<PluginEditorSnapshot?> ReadAsync(string relativePath, CancellationToken token);
    Task OpenAsync(string relativePath, int line, int column, CancellationToken token);
}
