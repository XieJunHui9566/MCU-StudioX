namespace StudioX.Application.CodeIntelligence;

using System.Text;
using StudioX.Foundation;
using StudioX.Packages;

/// <summary>工程内 Python 的静态导航；读取编辑快照与本地模块，不执行源码或加载解释器。</summary>
public sealed class PythonNavigationService
{
    public Task<IReadOnlyList<CodeLocation>> FindAsync(string root, string path, string text, int offset,
        bool references, IReadOnlyList<CodeDocumentSnapshot>? documents = null, MicroPythonProfile? profile = null,
        CancellationToken token = default) => Task.Run(async () =>
    {
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var files = new ProjectFileService();
        var buffers = (documents ?? []).Where(item => PythonAssistanceService.Supports(item.Path) &&
            !item.Path.StartsWith(MicroPythonApiDocuments.Prefix, StringComparison.Ordinal))
            .ToDictionary(item => item.Path.Replace('\\', '/'), item => item.Text, StringComparer.Ordinal);
        if (!path.StartsWith(MicroPythonApiDocuments.Prefix, StringComparison.Ordinal))
        {
            buffers[path] = text;
        }
        var pending = new Stack<string>();
        pending.Push("");
        var length = 0;
        var visited = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if (++visited > 2048)
            {
                throw new StudioXException("PYTHON_INDEX_LIMIT", "Python 工程目录超过导航扫描上限。");
            }
            foreach (var item in files.List(root, directory))
            {
                token.ThrowIfCancellationRequested();
                if (item.IsLink || item.Name.StartsWith('.') || item.Name is "__pycache__" or "device" or "node_modules" or ".venv" or "venv")
                {
                    continue;
                }
                if (item.IsDirectory)
                {
                    pending.Push(item.RelativePath);
                }
                else if (PythonAssistanceService.Supports(item.RelativePath))
                {
                    var content = buffers.GetValueOrDefault(item.RelativePath) ?? (await files.ReadAsync(root, item.RelativePath, token).ConfigureAwait(false)).Text;
                    Add(item.RelativePath, content);
                }
            }
        }
        foreach (var buffer in buffers)
        {
            _ = PathBoundary.Resolve(root, buffer.Key);
            if (!sources.ContainsKey(buffer.Key))
            {
                Add(buffer.Key, buffer.Value);
            }
        }
        foreach (var api in MicroPythonApiDocuments.Create(profile))
        {
            sources[api.Key] = api.Value;
        }
        return new PythonSymbolIndex(sources, token).Find(path, offset, references);

        void Add(string name, string content)
        {
            length += content.Length;
            if (content.Length > 1024 * 1024 || length > 8 * 1024 * 1024 || sources.Count >= 512)
            {
                throw new StudioXException("PYTHON_INDEX_LIMIT", "Python 导航支持最多 512 个文件、单文件 1 MiB、工程文本总计 8 MiB。");
            }
            sources[name] = content;
        }
    }, token);

    public Task<SourceDocument> ReadAsync(string root, string path, MicroPythonProfile? profile, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (path.StartsWith(MicroPythonApiDocuments.Prefix, StringComparison.Ordinal))
        {
            var text = MicroPythonApiDocuments.Create(profile).GetValueOrDefault(path)
                ?? throw new StudioXException("PYTHON_API", "当前工程没有对应的 MicroPython API 声明。");
            return Task.FromResult(new SourceDocument(path, text, Encoding.UTF8, "", true, "只读 · MicroPython API 声明（非固件实现）"));
        }
        return new ProjectFileService().ReadAsync(root, path, token);
    }
}
