using CodeSharp.Core;

namespace CodeSharp.Api;

public record ToolDefinition(
    string Name,
    string? Description = null,
    object? InputSchema = null
);

public record MessageRequest(
    string Model,
    IReadOnlyList<InputMessage> Messages,
    IReadOnlyList<ToolDefinition>? Tools = null,
    string? SystemPrompt = null,
    int? MaxTokens = null,
    ToolChoice? ToolChoice = null
);

public record InputMessage(
    string Role,
    IReadOnlyList<InputContentBlock> Content
);

public record InputContentBlock
{
    public sealed record TextBlock(string Text) : InputContentBlock;

    public sealed record Image(string Type, string Source) : InputContentBlock;

    public sealed record ToolUse(string Id, string Name, string Input) : InputContentBlock;

    public sealed record ToolResultBlock(
        string ToolUseId,
        string Content,
        bool? IsError = null
    ) : InputContentBlock;
}

public enum ToolChoice
{
    Auto,
    Any,
    None,
    Required,
}

public record MessageResponse(
    string Id,
    string Model,
    string Role,
    IReadOnlyList<OutputContentBlock> Content,
    TokenUsage Usage,
    string? StopReason = null
);

public record OutputContentBlock
{
    public sealed record TextBlock(string Text) : OutputContentBlock;

    public sealed record ToolUse(string Id, string Name, string Input) : OutputContentBlock;
}

public record TokenUsageResponse(
    long InputTokens,
    long OutputTokens,
    long? CacheCreationInputTokens = null,
    long? CacheReadInputTokens = null
);

public record StreamEvent
{
    public sealed record MessageStart(string Id, string Model, TokenUsageResponse Usage) : StreamEvent;
    
    public sealed record ContentBlockStart(int Index, string Type, string? Id = null, string? Name = null) : StreamEvent;
    
    public sealed record ContentBlockDelta(int Index, ContentBlockDeltaContent Delta) : StreamEvent;
    
    public sealed record ContentBlockStop(int Index) : StreamEvent;
    
    public sealed record MessageDelta(TokenUsageResponse Usage, string? StopReason = null) : StreamEvent;
    
    public sealed record MessageStop() : StreamEvent;
}

public record ContentBlockDeltaContent
{
    public sealed record TextDelta(string Text) : ContentBlockDeltaContent;
    
    public sealed record InputJsonDelta(string PartialJson) : ContentBlockDeltaContent;
}
