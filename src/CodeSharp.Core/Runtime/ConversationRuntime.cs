namespace CodeSharp.Core;

public record ConversationRuntimeCheckpoint(int MessageCount, UsageTrackerSnapshot Usage);

public class ConversationRuntime
{
    private readonly Session _session;
    private readonly IApiClient _apiClient;
    private readonly IToolExecutor _toolExecutor;
    private readonly PermissionPolicy _permissionPolicy;
    private readonly IReadOnlyList<string> _systemPrompt;
    private readonly UsageTracker _usageTracker;
    private readonly HookRunner _hookRunner;
    private readonly int _maxIterations;
    
    public const string UserInterruptMessage = "request interrupted by user";
    
    public ConversationRuntime(
        Session session,
        IApiClient apiClient,
        IToolExecutor toolExecutor,
        PermissionPolicy permissionPolicy,
        IReadOnlyList<string> systemPrompt,
        int maxIterations = int.MaxValue
    )
    {
        _session = session;
        _apiClient = apiClient;
        _toolExecutor = toolExecutor;
        _permissionPolicy = permissionPolicy;
        _systemPrompt = systemPrompt;
        _maxIterations = maxIterations;
        _usageTracker = UsageTracker.FromSession(session);
        _hookRunner = HookRunner.Default;
    }
    
    public Session Session => _session;
    public UsageTracker Usage => _usageTracker;

    public ConversationRuntimeCheckpoint CaptureCheckpoint() =>
        new(_session.Count, _usageTracker.Snapshot());

    public void RestoreCheckpoint(ConversationRuntimeCheckpoint checkpoint)
    {
        _session.Truncate(checkpoint.MessageCount);
        _usageTracker.Restore(checkpoint.Usage);
    }
    
    public async Task<TurnSummary> RunTurnAsync(
        string userInput,
        IPermissionPrompter? prompter = null,
        Action<RuntimeActivity>? activitySink = null,
        CancellationToken cancellationToken = default
    )
    {
        _session.AddMessage(ConversationMessage.UserText(userInput));
        
        var assistantMessages = new List<ConversationMessage>();
        var toolResults = new List<ConversationMessage>();
        var iterations = 0;
        
        while (true)
        {
            iterations++;
            if (iterations > _maxIterations)
            {
                throw new RuntimeError("conversation loop exceeded the maximum number of iterations");
            }
            
            var request = new ApiRequest(_systemPrompt, _session.Messages);
            var events = await _apiClient.StreamAsync(request, cancellationToken);
            
            var (assistantMessage, usage) = BuildAssistantMessage(events);
            if (usage is not null)
            {
                _usageTracker.Record(usage);
            }
            
            var pendingToolUses = assistantMessage.Blocks
                .OfType<ContentBlock.ToolUse>()
                .Select(tu => (tu.Id, tu.Name, tu.Input))
                .ToList();
            
            _session.AddMessage(assistantMessage);
            assistantMessages.Add(assistantMessage);
            
            if (pendingToolUses.Count == 0)
            {
                break;
            }
            
            foreach (var (toolUseId, toolName, input) in pendingToolUses)
            {
                var permissionResult = prompter is not null
                    ? _permissionPolicy.Authorize(toolName, input, prompter)
                    : _permissionPolicy.Authorize(toolName, input);
                
                ConversationMessage resultMessage;
                
                if (permissionResult.Outcome == PermissionOutcome.Allow)
                {
                    var preHookResult = await _hookRunner.RunPreToolUseAsync(toolName, input, cancellationToken);
                    
                    if (preHookResult.IsDenied)
                    {
                        var denyMessage = $"PreToolUse hook denied tool `{toolName}`";
                        resultMessage = ConversationMessage.ToolResult(
                            toolUseId,
                            toolName,
                            FormatHookMessage(preHookResult, denyMessage),
                            true
                        );
                    }
                    else
                    {
                        activitySink?.Invoke(new RuntimeActivity.ToolStarted(toolName, input));
                        var (output, isError) = await ExecuteToolWithErrorHandlingAsync(
                            toolName, input, cancellationToken
                        );
                        activitySink?.Invoke(new RuntimeActivity.ToolFinished(toolName, isError));
                        
                        output = MergeHookFeedback(preHookResult.Messages, output, false);
                        
                        var postHookResult = await _hookRunner.RunPostToolUseAsync(
                            toolName, input, output, isError, cancellationToken
                        );
                        
                        if (postHookResult.IsDenied)
                        {
                            isError = true;
                        }
                        
                        output = MergeHookFeedback(postHookResult.Messages, output, postHookResult.IsDenied);
                        
                        resultMessage = ConversationMessage.ToolResult(toolUseId, toolName, output, isError);
                    }
                }
                else if (permissionResult.Reason == UserInterruptMessage)
                {
                    throw new RuntimeError(UserInterruptMessage);
                }
                else
                {
                    activitySink?.Invoke(new RuntimeActivity.ToolBlocked(
                        toolName,
                        permissionResult.Reason ?? "Permission denied"
                    ));
                    resultMessage = ConversationMessage.ToolResult(
                        toolUseId,
                        toolName,
                        permissionResult.Reason ?? "Permission denied",
                        true
                    );
                }
                
                _session.AddMessage(resultMessage);
                toolResults.Add(resultMessage);
            }
        }
        
        return new TurnSummary(
            assistantMessages,
            toolResults,
            iterations,
            _usageTracker.CumulativeUsage()
        );
    }
    
    private async Task<(string Output, bool IsError)> ExecuteToolWithErrorHandlingAsync(
        string toolName,
        string input,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var result = await _toolExecutor.ExecuteAsync(toolName, input, cancellationToken);
            return (result.Output, result.IsError);
        }
        catch (Exception ex) when (ex.Message == UserInterruptMessage)
        {
            throw new RuntimeError(UserInterruptMessage);
        }
        catch (Exception ex)
        {
            return (ex.Message, true);
        }
    }
    
    private static (ConversationMessage Message, TokenUsage? Usage) BuildAssistantMessage(
        IReadOnlyList<AssistantEvent> events
    )
    {
        var text = string.Empty;
        var blocks = new List<ContentBlock>();
        var finished = false;
        TokenUsage? usage = null;
        
        foreach (var ev in events)
        {
            switch (ev)
            {
                case AssistantEvent.TextDelta td:
                    text += td.Delta;
                    break;
                    
                case AssistantEvent.ToolUse tu:
                    if (!string.IsNullOrEmpty(text))
                    {
                        blocks.Add(new ContentBlock.Text(text));
                        text = string.Empty;
                    }
                    blocks.Add(new ContentBlock.ToolUse(tu.Id, tu.Name, tu.Input));
                    break;
                    
                case AssistantEvent.Usage u:
                    usage = u.TokenUsage;
                    break;
                    
                case AssistantEvent.MessageStop:
                    finished = true;
                    break;
            }
        }
        
        if (!string.IsNullOrEmpty(text))
        {
            blocks.Add(new ContentBlock.Text(text));
        }
        
        if (!finished)
        {
            throw new RuntimeError("assistant stream ended without a message stop event");
        }
        
        if (blocks.Count == 0)
        {
            throw new RuntimeError("assistant stream produced no content");
        }
        
        return (ConversationMessage.AssistantWithUsage(blocks, usage), usage);
    }
    
    private static string FormatHookMessage(HookRunResult result, string fallback) =>
        result.Messages.Count == 0 ? fallback : string.Join("\n", result.Messages);
    
    private static string MergeHookFeedback(IReadOnlyList<string> messages, string output, bool denied)
    {
        if (messages.Count == 0)
            return output;
        
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(output))
        {
            sections.Add(output);
        }
        
        var label = denied ? "Hook feedback (denied)" : "Hook feedback";
        sections.Add($"{label}:\n{string.Join("\n", messages)}");
        
        return string.Join("\n\n", sections);
    }
}
