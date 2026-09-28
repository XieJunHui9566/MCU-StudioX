namespace StudioX.Desktop;

using StudioX.Application.Plugins;

/// <summary>把受工程代次校验的编辑器快照和导航委托交给应用代理，不跨线程读取 WPF 缓冲区。</summary>
internal sealed class DesktopPluginEditorAccess(
    Func<string, CancellationToken, Task<PluginEditorSnapshot?>> read,
    Func<string, int, int, CancellationToken, Task> open) : IPluginEditorAccess
{
    public Task<PluginEditorSnapshot?> ReadAsync(string relative, CancellationToken token) => read(relative, token);
    public Task OpenAsync(string relative, int line, int column, CancellationToken token) => open(relative, line, column, token);
}
