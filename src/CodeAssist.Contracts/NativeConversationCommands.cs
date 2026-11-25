namespace CodeAssist.Contracts;

public static class NativeConversationCommands
{
    public static IReadOnlyList<ConversationCommandDto> Items { get; } = Array.AsReadOnly<ConversationCommandDto>([
        new("help", "List native commands and admitted prompt guidance.", "native", "host"),
        new("history", "Read committed conversation messages.", "native", "host"),
        new("exit", "Leave chat without deleting the conversation.", "native", "host"),
        new("session.new", "Start a conversation using the default or specified model.", "native", "host", "[model]"),
        new("session.list", "List conversations in the attached workspace service.", "native", "host"),
        new("session.use", "Select an existing conversation.", "native", "host", "id"),
        new("session.fork", "Branch committed history into an independent conversation.", "native", "host"),
        new("session.rename", "Change the current conversation title.", "native", "host", "title"),
        new("session.archive", "Archive the conversation and block new turns.", "native", "host"),
        new("session.restore", "Unarchive the conversation.", "native", "host"),
        new("session.info", "Read conversation metadata and usage counters.", "native", "host"),
        new("session.checkpoints", "List retained committed conversation revisions.", "native", "host"),
        new("session.rewind", "Restore conversation history, not filesystem changes.", "native", "host", "revision"),
        new("usage", "Read reported token and turn counts; never estimate billing.", "native", "host"),
        new("context", "Inspect committed public history size, not hidden model context.", "native", "host"),
        new("models", "List configured and cached model metadata.", "native", "host"),
        new("skills", "List policy-admitted human skill guidance.", "native", "host"),
        new("plugins", "List enabled plugin prompt contributions.", "native", "host"),
        new("connectors", "Inspect session connector status without connecting.", "native", "host"),
        new("status", "Check attached service identity and readiness.", "native", "host"),
    ]);
}