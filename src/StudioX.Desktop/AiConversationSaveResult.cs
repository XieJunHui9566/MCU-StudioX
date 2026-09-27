namespace StudioX.Desktop;

using StudioX.Application;

/// <summary>保存失败不丢弃模型答复；调用方仍可展示内存快照和原始错误。</summary>
internal sealed record AiConversationSaveResult(AiConversation Conversation, Exception? Error);
