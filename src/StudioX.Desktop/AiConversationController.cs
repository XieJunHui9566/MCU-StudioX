namespace StudioX.Desktop;

using StudioX.Application;

/// <summary>拥有工程级对话和草稿；异步持久化返回后校验代次，避免旧工程覆盖新工程的历史。</summary>
internal sealed class AiConversationController(AiConversationStore store)
{
    private readonly Dictionary<string, string> drafts = new(StringComparer.Ordinal);

    public string? Project
    {
        get; private set;
    }
    public int Generation
    {
        get; private set;
    }
    public IReadOnlyList<AiConversation> Conversations { get; private set; } = [];
    public AiConversation? Active
    {
        get; private set;
    }
    public IReadOnlyList<AiAgentTurn> History => Active?.Turns ?? [];
    public bool SaveFailed
    {
        get; private set;
    }

    public int BindProject(string? project)
    {
        Project = project;
        Generation++;
        Conversations = [];
        Active = null;
        SaveFailed = false;
        drafts.Clear();
        return Generation;
    }

    public bool IsCurrent(string project, int generation) =>
        generation == Generation && string.Equals(Project, project, StringComparison.OrdinalIgnoreCase);

    public string Select(AiConversation? conversation, string currentDraft, bool projectChanged = false)
    {
        var selectedDraft = currentDraft;
        if (projectChanged)
        {
            drafts.Clear();
            selectedDraft = "";
        }
        else if (Active?.Id != conversation?.Id)
        {
            drafts[DraftKey(Active)] = currentDraft;
            selectedDraft = drafts.GetValueOrDefault(DraftKey(conversation), "");
        }

        Active = conversation;
        SaveFailed = false;
        return selectedDraft;
    }

    public async Task<bool> LoadAsync(string project, int generation)
    {
        var loaded = await store.ListAsync(project);
        if (!IsCurrent(project, generation))
        {
            return false;
        }

        Conversations = loaded;
        return true;
    }

    public async Task<AiConversation> CreateAsync(string project, int generation)
    {
        var created = await store.CreateAsync(project);
        if (IsCurrent(project, generation))
        {
            Replace(created);
        }

        return created;
    }

    public async Task DeleteAsync(string project, int generation, AiConversation conversation)
    {
        await store.DeleteAsync(project, conversation.Id);
        if (IsCurrent(project, generation))
        {
            Conversations = Conversations.Where(item => item.Id != conversation.Id).ToArray();
            drafts.Remove(DraftKey(conversation));
        }
    }

    public void ForgetNewConversationDraft() => drafts.Remove(DraftKey(null));

    public async Task<AiConversationSaveResult> SaveReplyAsync(
        string project,
        int generation,
        AiConversation conversation,
        string prompt,
        AiAgentTurn turn,
        AiAgentReply reply)
    {
        var updated = conversation with
        {
            Title = conversation.Turns.Count == 0 ? CreateTitle(prompt) : conversation.Title,
            Turns = [.. conversation.Turns, turn],
            LastPromptTokens = reply.Usage?.PromptTokens,
            LastCompletionTokens = reply.Usage?.CompletionTokens,
            LastPromptCacheHitTokens = reply.AggregateUsage?.PromptCacheHitTokens,
            LastPromptCacheMissTokens = reply.AggregateUsage?.PromptCacheMissTokens
        };

        Exception? saveError = null;
        try
        {
            updated = await store.SaveAsync(project, updated);
            if (IsCurrent(project, generation))
            {
                Replace(updated);
            }
        }
        catch (Exception error)
        {
            // 持久化失败仍保留完整本轮答复供复制，同时阻止继续发送使未保存历史被覆盖。
            saveError = error;
            if (IsCurrent(project, generation))
            {
                SaveFailed = true;
            }
        }

        if (IsCurrent(project, generation))
        {
            Active = updated;
        }

        return new(updated, saveError);
    }

    private void Replace(AiConversation conversation) =>
        Conversations = [conversation, .. Conversations.Where(item => item.Id != conversation.Id)];

    private static string DraftKey(AiConversation? conversation) => conversation?.Id ?? "new";

    private static string CreateTitle(string prompt)
    {
        var title = new string(prompt.Where(character => !char.IsControl(character)).Take(40).ToArray()).Trim();
        return title.Length == 0 ? "新对话" : title;
    }
}
