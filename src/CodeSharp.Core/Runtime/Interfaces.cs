namespace CodeSharp.Core;

public record ApiRequest(
    IReadOnlyList<string> SystemPrompt,
    IReadOnlyList<ConversationMessage> Messages
);

public record AssistantEvent
{
    public sealed record TextDelta(string Delta) : AssistantEvent;
    
    public sealed record ToolUse(string Id, string Name, string Input) : AssistantEvent;
    
    public sealed record Usage(TokenUsage TokenUsage) : AssistantEvent;
    
    public sealed record MessageStop : AssistantEvent;
}

public record TurnSummary(
    IReadOnlyList<ConversationMessage> AssistantMessages,
    IReadOnlyList<ConversationMessage> ToolResults,
    int Iterations,
    TokenUsage Usage
);

public abstract record RuntimeActivity
{
    public sealed record ToolStarted(string ToolName, string Input) : RuntimeActivity;

    public sealed record ToolFinished(string ToolName, bool IsError) : RuntimeActivity;

    public sealed record ToolBlocked(string ToolName, string Reason) : RuntimeActivity;
}

public interface IApiClient
{
    Task<IReadOnlyList<AssistantEvent>> StreamAsync(ApiRequest request, CancellationToken cancellationToken = default);
}

public interface IToolExecutor
{
    Task<ToolResult> ExecuteAsync(string toolName, string input, CancellationToken cancellationToken = default);
}

public record ToolResult(string Output, bool IsError = false);

public class ToolError : Exception
{
    public ToolError(string message) : base(message) { }
}

public class RuntimeError : Exception
{
    public RuntimeError(string message) : base(message) { }
}
