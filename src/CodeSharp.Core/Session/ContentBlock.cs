namespace CodeSharp.Core;

public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

public abstract record ContentBlock
{
    public sealed record Text(string Content) : ContentBlock;
    
    public sealed record ToolUse(string Id, string Name, string Input) : ContentBlock;
    
    public sealed record ToolResult(
        string ToolUseId,
        string ToolName,
        string Output,
        bool IsError
    ) : ContentBlock;
}

public record ConversationMessage(
    MessageRole Role,
    IReadOnlyList<ContentBlock> Blocks,
    TokenUsage? Usage = null
)
{
    public static ConversationMessage UserText(string content) =>
        new(MessageRole.User, [new ContentBlock.Text(content)]);
    
    public static ConversationMessage AssistantText(string content) =>
        new(MessageRole.Assistant, [new ContentBlock.Text(content)]);
    
    public static ConversationMessage AssistantWithUsage(
        IReadOnlyList<ContentBlock> blocks,
        TokenUsage? usage = null
    ) => new(MessageRole.Assistant, blocks, usage);
    
    public static ConversationMessage ToolResult(
        string toolUseId,
        string toolName,
        string output,
        bool isError
    ) => new(
        MessageRole.Tool,
        [new ContentBlock.ToolResult(toolUseId, toolName, output, isError)]
    );
    
    public static ConversationMessage System(string content) =>
        new(MessageRole.System, [new ContentBlock.Text(content)]);
}

public record TokenUsage(
    long InputTokens,
    long OutputTokens,
    long CacheCreationInputTokens = 0,
    long CacheReadInputTokens = 0
)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheCreationInputTokens + CacheReadInputTokens;
}
