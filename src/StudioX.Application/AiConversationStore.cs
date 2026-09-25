namespace StudioX.Application;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using StudioX.Foundation;

/// <summary>包含可见对话及协议回放消息；工具结果可含工程文本，凭据不进入历史文件。</summary>
public sealed record AiConversation(string Id, string Title, DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc, IReadOnlyList<AiAgentTurn> Turns,
    int? LastPromptTokens = null, int? LastCompletionTokens = null,
    long? LastPromptCacheHitTokens = null, long? LastPromptCacheMissTokens = null);

/// <summary>把每个工程的对话历史隔离保存到用户数据目录，避免工程与构建产物膨胀。</summary>
public sealed class AiConversationStore
{
    public const int MaxConversationsPerProject = 64;
    public const int MaxConversationBytes = 512 * 1024;
    public const int MaxTurnsPerConversation = 256;

    private const int MaxTitleCharacters = 120;
    private const int MaxUserCharacters = 4_000;
    private const int MaxAssistantCharacters = 16_000;
    private readonly string dataDirectory;
    private readonly SemaphoreSlim gate = new(1, 1);

    public AiConversationStore(string dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
            throw new ArgumentException("用户数据目录必须是绝对路径。", nameof(dataDirectory));
        this.dataDirectory = Path.GetFullPath(dataDirectory);
    }

    public async Task<AiConversation> CreateAsync(string project, CancellationToken token = default)
    {
        var directory = ProjectDirectory(project, create: true);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var existing = ConversationFiles(directory).ToArray();
            if (existing.Length >= MaxConversationsPerProject)
                throw new StudioXException("AI_HISTORY_COUNT", "当前工程已保存 64 个对话，请先手动清理旧对话。");
            var now = DateTimeOffset.UtcNow;
            var conversation = new AiConversation(Guid.NewGuid().ToString("N"), "新对话", now, now, []);
            await WriteAsync(ConversationPath(directory, conversation.Id), conversation, overwrite: false, token)
                .ConfigureAwait(false);
            return conversation;
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<AiConversation>> ListAsync(string project, CancellationToken token = default)
    {
        var directory = ProjectDirectory(project, create: false);
        if (!Directory.Exists(directory)) return [];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var paths = ConversationFiles(directory).ToArray();
            if (paths.Length > MaxConversationsPerProject)
                throw new StudioXException("AI_HISTORY_COUNT", "当前工程的对话数量已超过 64 个，请手动检查历史目录。");
            var conversations = new List<AiConversation>(paths.Length);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                conversations.Add(await ReadAsync(path, token).ConfigureAwait(false));
            }
            return conversations.OrderByDescending(item => item.UpdatedUtc)
                .ThenByDescending(item => item.CreatedUtc).ToArray();
        }
        finally { gate.Release(); }
    }

    public async Task<AiConversation> LoadAsync(string project, string id, CancellationToken token = default)
    {
        var directory = ProjectDirectory(project, create: false);
        var path = ConversationPath(directory, id);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ReadAsync(path, token).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    /// <summary>仅更新 CreateAsync 创建过的对话；返回带最新更新时间的持久化快照。</summary>
    public async Task<AiConversation> SaveAsync(string project, AiConversation conversation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        var directory = ProjectDirectory(project, create: false);
        var path = ConversationPath(directory, conversation.Id);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var existing = await ReadAsync(path, token).ConfigureAwait(false);
            if (conversation.CreatedUtc != existing.CreatedUtc)
                throw new StudioXException("AI_HISTORY_ID", "对话创建时间与已保存的记录不一致。");
            var updated = conversation with { UpdatedUtc = DateTimeOffset.UtcNow };
            Validate(updated);
            var stored = CompactForStorage(updated);
            await WriteAsync(path, stored, overwrite: true, token).ConfigureAwait(false);
            return stored;
        }
        finally { gate.Release(); }
    }

    /// <summary>仅在用户明确选择删除某条历史时调用；创建新对话不会自动清理旧记录。</summary>
    public async Task DeleteAsync(string project, string id, CancellationToken token = default)
    {
        var directory = ProjectDirectory(project, create: false);
        var path = ConversationPath(directory, id);
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (!File.Exists(path))
                throw new StudioXException("AI_HISTORY_MISSING", "找不到所选对话。");
            RejectReparsePoint(path);
            File.Delete(path);
        }
        finally { gate.Release(); }
    }

    private string ProjectDirectory(string project, bool create)
    {
        if (string.IsNullOrWhiteSpace(project) || !Path.IsPathFullyQualified(project))
            throw new StudioXException("AI_PROJECT", "请先选择绝对路径的工程目录。");
        string normalized;
        try { normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new StudioXException("AI_PROJECT", "工程目录路径无效。", error); }
        if (!Directory.Exists(normalized))
            throw new StudioXException("AI_PROJECT", "工程目录不存在。");
        RejectReparseAncestors(normalized);

        var identity = OperatingSystem.IsWindows() ? normalized.ToUpperInvariant() : normalized;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        var root = Path.Combine(dataDirectory, "ai-conversations");
        var directory = Path.Combine(root, hash);
        RejectReparseAncestors(dataDirectory);
        if (create)
        {
            Directory.CreateDirectory(dataDirectory);
            RejectReparsePoint(dataDirectory);
            Directory.CreateDirectory(root);
            RejectReparsePoint(root);
            Directory.CreateDirectory(directory);
        }
        else
        {
            RejectReparsePoint(dataDirectory);
            RejectReparsePoint(root);
        }
        RejectReparsePoint(directory);
        return directory;
    }

    private static IEnumerable<string> ConversationFiles(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            _ = ConversationPath(directory, Path.GetFileNameWithoutExtension(path));
            RejectReparsePoint(path);
            yield return path;
        }
    }

    private static string ConversationPath(string directory, string id)
    {
        if (id is null || id.Length != 32 || id.Any(character =>
                character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')) ||
            !Guid.TryParseExact(id, "N", out _))
            throw new StudioXException("AI_HISTORY_ID", "对话 ID 必须是小写 GUID N 格式。");
        return Path.Combine(directory, id + ".json");
    }

    private static async Task<AiConversation> ReadAsync(string path, CancellationToken token)
    {
        if (!File.Exists(path))
            throw new StudioXException("AI_HISTORY_MISSING", "找不到所选对话。");
        RejectReparsePoint(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, useAsync: true);
        if (stream.Length > MaxConversationBytes)
            throw new StudioXException("AI_HISTORY_SIZE", "对话文件超过 512 KiB 上限，请手动检查历史目录。");
        var conversation = await JsonSerializer.DeserializeAsync<AiConversation>(stream, JsonStore.Options, token)
            .ConfigureAwait(false) ?? throw new StudioXException("AI_HISTORY_FORMAT", "对话文件内容为空。");
        var expectedId = Path.GetFileNameWithoutExtension(path);
        if (!string.Equals(conversation.Id, expectedId, StringComparison.Ordinal))
            throw new StudioXException("AI_HISTORY_ID", "对话文件名与内容中的 ID 不一致。");
        Validate(conversation);
        return conversation;
    }

    private static async Task WriteAsync(string path, AiConversation conversation, bool overwrite, CancellationToken token)
    {
        Validate(conversation);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(conversation, JsonStore.Options);
        if (bytes.Length > MaxConversationBytes)
            throw new StudioXException("AI_HISTORY_SIZE", "对话达到 512 KiB 上限，请新建对话继续，不会自动丢弃历史。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await stream.WriteAsync(bytes, token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static AiConversation CompactForStorage(AiConversation conversation)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(conversation, JsonStore.Options).Length <= MaxConversationBytes)
            return conversation;

        // 可见问答是历史主体；从最旧轮开始舍弃协议回放和推理文本，给新近轮次保留更多上下文。
        var turns = conversation.Turns.ToArray();
        for (var index = 0; index < turns.Length; index++)
        {
            var turn = turns[index];
            if (turn.ProtocolMessages is null && turn.ReasoningContent is null) continue;
            turns[index] = turn with { ProtocolMessages = null, ReasoningContent = null };
            var candidate = conversation with { Turns = turns };
            if (JsonSerializer.SerializeToUtf8Bytes(candidate, JsonStore.Options).Length <= MaxConversationBytes)
                return candidate;
        }

        throw new StudioXException("AI_HISTORY_SIZE", "对话可见消息达到 512 KiB 上限，请新建对话继续；旧历史未被覆盖。");
    }

    private static void Validate(AiConversation conversation)
    {
        _ = ConversationPath("", conversation.Id);
        if (conversation.Turns is null)
            throw new StudioXException("AI_HISTORY_FORMAT", "对话消息列表无效。");
        if (conversation.Turns.Count > MaxTurnsPerConversation)
            throw new StudioXException("AI_HISTORY_TURNS", "单个对话最多保存 256 轮，请新建对话继续，已有历史不会被丢弃。");
        if (string.IsNullOrWhiteSpace(conversation.Title) || conversation.Title.Length > MaxTitleCharacters ||
            conversation.Title.Any(char.IsControl) ||
            conversation.CreatedUtc.Offset != TimeSpan.Zero || conversation.UpdatedUtc.Offset != TimeSpan.Zero ||
            conversation.UpdatedUtc < conversation.CreatedUtc ||
            conversation.LastPromptTokens is < 0 || conversation.LastCompletionTokens is < 0 ||
            conversation.LastPromptCacheHitTokens is < 0 || conversation.LastPromptCacheMissTokens is < 0)
            throw new StudioXException("AI_HISTORY_FORMAT", "对话标题、时间或用量无效。");
        foreach (var turn in conversation.Turns)
            if (turn is null || string.IsNullOrWhiteSpace(turn.User) || turn.User.Length > MaxUserCharacters ||
                string.IsNullOrWhiteSpace(turn.Assistant) || turn.Assistant.Length > MaxAssistantCharacters ||
                turn.SteeringMessages?.Any(message => string.IsNullOrWhiteSpace(message) || message.Length > MaxUserCharacters) == true)
                throw new StudioXException("AI_HISTORY_FORMAT", "对话中有无效或过长的消息。");
    }

    private static void RejectReparsePoint(string path)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new StudioXException("AI_HISTORY_PATH", "工程或对话历史路径不能经过链接。");
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    private static void RejectReparseAncestors(string path)
    {
        var current = path;
        while (current is not null)
        {
            RejectReparsePoint(current);
            current = Directory.GetParent(current)?.FullName;
        }
    }
}
