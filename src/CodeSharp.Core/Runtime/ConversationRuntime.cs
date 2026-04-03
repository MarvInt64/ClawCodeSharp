using System.Text;
using System.Text.Json;

namespace CodeSharp.Core;

public record ConversationRuntimeCheckpoint(int MessageCount, UsageTrackerSnapshot Usage);

public class ConversationRuntime
{
    private static readonly HashSet<string> ParallelSafeTools = new(StringComparer.Ordinal)
    {
        "read_file",
        "glob_search",
        "grep_search"
    };

    private static readonly HashSet<string> MutatingFileTools = new(StringComparer.Ordinal)
    {
        "write_file",
        "edit_file"
    };

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
            var liveAssistantText = new StringBuilder();
            var lastDraft = string.Empty;
            var events = await _apiClient.StreamAsync(
                request,
                assistantEvent =>
                {
                    if (assistantEvent is not AssistantEvent.TextDelta textDelta)
                    {
                        return;
                    }

                    liveAssistantText.Append(textDelta.Delta);
                    var normalized = NormalizeAssistantLiveText(liveAssistantText.ToString());
                    if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, lastDraft, StringComparison.Ordinal))
                    {
                        return;
                    }

                    lastDraft = normalized;
                    activitySink?.Invoke(new RuntimeActivity.AssistantDraft(normalized));
                },
                cancellationToken
            );
            
            var (assistantMessage, usage) = BuildAssistantMessage(events);
            if (usage is not null)
            {
                _usageTracker.Record(usage);
            }
            
            var pendingToolUses = assistantMessage.Blocks
                .OfType<ContentBlock.ToolUse>()
                .Select(tu => (tu.Id, tu.Name, tu.Input))
                .ToList();

            ValidateToolUseInputs(pendingToolUses);
            
            _session.AddMessage(assistantMessage);
            assistantMessages.Add(assistantMessage);

            if (pendingToolUses.Count > 0 && BuildAssistantPlanPreview(assistantMessage, pendingToolUses) is { } planPreview)
            {
                activitySink?.Invoke(new RuntimeActivity.AssistantPlan(planPreview));
            }
            
            if (pendingToolUses.Count == 0)
            {
                break;
            }

            var iterationResults = await ExecutePendingToolUsesAsync(
                pendingToolUses,
                prompter,
                activitySink,
                cancellationToken
            );

            foreach (var resultMessage in iterationResults)
            {
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

    private async Task<IReadOnlyList<ConversationMessage>> ExecutePendingToolUsesAsync(
        IReadOnlyList<(string ToolUseId, string ToolName, string Input)> pendingToolUses,
        IPermissionPrompter? prompter,
        Action<RuntimeActivity>? activitySink,
        CancellationToken cancellationToken
    )
    {
        var results = new ConversationMessage[pendingToolUses.Count];
        var parallelBatch = new List<PreparedToolExecution>();
        var mutatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < pendingToolUses.Count; index++)
        {
            var (toolUseId, toolName, input) = pendingToolUses[index];
            var prepared = await PrepareToolExecutionAsync(
                index,
                toolUseId,
                toolName,
                input,
                prompter,
                activitySink,
                cancellationToken
            );

            if (IsParallelSafeTool(toolName))
            {
                parallelBatch.Add(prepared);
                continue;
            }

            await FlushParallelBatchAsync(parallelBatch, results, cancellationToken);

            if (TryGetMutatingToolPath(toolName, input) is { } path &&
                mutatedPaths.Contains(path))
            {
                var message = $"`{DisplayPath(path)}` was already modified earlier in this assistant step. Re-read it in the next step before applying another edit.";
                activitySink?.Invoke(new RuntimeActivity.ToolStarted(toolUseId, toolName, input));
                activitySink?.Invoke(new RuntimeActivity.ToolFinished(toolUseId, toolName, message, true));
                results[index] = ConversationMessage.ToolResult(toolUseId, toolName, message, true);
                continue;
            }

            results[index] = await prepared.ExecuteAsync(cancellationToken);

            if (TryGetMutatingToolPath(toolName, input) is { } mutatedPath &&
                IsSuccessfulToolResult(results[index]))
            {
                mutatedPaths.Add(mutatedPath);
            }
        }

        await FlushParallelBatchAsync(parallelBatch, results, cancellationToken);

        return results;
    }

    private async Task<PreparedToolExecution> PrepareToolExecutionAsync(
        int index,
        string toolUseId,
        string toolName,
        string input,
        IPermissionPrompter? prompter,
        Action<RuntimeActivity>? activitySink,
        CancellationToken cancellationToken
    )
    {
        var permissionResult = prompter is not null
            ? _permissionPolicy.Authorize(toolName, input, prompter)
            : _permissionPolicy.Authorize(toolName, input);

        if (permissionResult.Outcome != PermissionOutcome.Allow)
        {
            if (permissionResult.Reason == UserInterruptMessage)
            {
                throw new RuntimeError(UserInterruptMessage);
            }

            activitySink?.Invoke(new RuntimeActivity.ToolBlocked(
                toolUseId,
                toolName,
                permissionResult.Reason ?? "Permission denied"
            ));

            var blocked = ConversationMessage.ToolResult(
                toolUseId,
                toolName,
                permissionResult.Reason ?? "Permission denied",
                true
            );

            return new PreparedToolExecution(index, _ => Task.FromResult(blocked));
        }

        var preHookResult = await _hookRunner.RunPreToolUseAsync(toolName, input, cancellationToken);
        if (preHookResult.IsDenied)
        {
            var denyMessage = $"PreToolUse hook denied tool `{toolName}`";
            var denied = ConversationMessage.ToolResult(
                toolUseId,
                toolName,
                FormatHookMessage(preHookResult, denyMessage),
                true
            );

            return new PreparedToolExecution(index, _ => Task.FromResult(denied));
        }

        return new PreparedToolExecution(
            index,
            ct => ExecutePreparedToolAsync(toolUseId, toolName, input, preHookResult, activitySink, ct)
        );
    }

    private async Task<ConversationMessage> ExecutePreparedToolAsync(
        string toolUseId,
        string toolName,
        string input,
        HookRunResult preHookResult,
        Action<RuntimeActivity>? activitySink,
        CancellationToken cancellationToken
    )
    {
        activitySink?.Invoke(new RuntimeActivity.ToolStarted(toolUseId, toolName, input));
        var (output, isError) = await ExecuteToolWithErrorHandlingAsync(toolName, input, cancellationToken);
        activitySink?.Invoke(new RuntimeActivity.ToolFinished(toolUseId, toolName, output, isError));

        output = MergeHookFeedback(preHookResult.Messages, output, false);

        var postHookResult = await _hookRunner.RunPostToolUseAsync(
            toolName,
            input,
            output,
            isError,
            cancellationToken
        );

        if (postHookResult.IsDenied)
        {
            isError = true;
        }

        output = MergeHookFeedback(postHookResult.Messages, output, postHookResult.IsDenied);

        return ConversationMessage.ToolResult(toolUseId, toolName, output, isError);
    }

    private static bool IsParallelSafeTool(string toolName) => ParallelSafeTools.Contains(toolName);

    private string? TryGetMutatingToolPath(string toolName, string input)
    {
        if (!MutatingFileTools.Contains(toolName))
        {
            return null;
        }

        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(input);
            if (json.ValueKind != JsonValueKind.Object ||
                !json.TryGetProperty("path", out var pathElement) ||
                pathElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var path = pathElement.GetString();
            return string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path, Directory.GetCurrentDirectory());
        }
        catch
        {
            return null;
        }
    }

    private static bool IsSuccessfulToolResult(ConversationMessage message) =>
        message.Blocks
            .OfType<ContentBlock.ToolResult>()
            .Any(block => !block.IsError);

    private static string DisplayPath(string fullPath)
    {
        var cwd = Directory.GetCurrentDirectory();
        try
        {
            return Path.GetRelativePath(cwd, fullPath).Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    private static async Task FlushParallelBatchAsync(
        List<PreparedToolExecution> parallelBatch,
        ConversationMessage[] results,
        CancellationToken cancellationToken
    )
    {
        if (parallelBatch.Count == 0)
        {
            return;
        }

        var completed = await Task.WhenAll(parallelBatch.Select(run => run.RunAsync(cancellationToken)));
        foreach (var (index, message) in completed)
        {
            results[index] = message;
        }

        parallelBatch.Clear();
    }

    private static string? BuildAssistantPlanPreview(
        ConversationMessage message,
        IReadOnlyList<(string ToolUseId, string ToolName, string Input)> pendingToolUses
    )
    {
        var text = string.Join(
            "\n",
            message.Blocks
                .OfType<ContentBlock.Text>()
                .Select(block => block.Content.Trim())
                .Where(static content => !string.IsNullOrWhiteSpace(content))
        ).Trim();

        if (string.IsNullOrWhiteSpace(text))
        {
            return BuildToolPlanFallback(pendingToolUses);
        }

        var singleLine = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(singleLine))
        {
            return BuildToolPlanFallback(pendingToolUses);
        }

        if (!string.IsNullOrWhiteSpace(singleLine))
        {
            return singleLine;
        }

        return BuildToolPlanFallback(pendingToolUses);
    }

    private static string BuildToolPlanFallback(
        IReadOnlyList<(string ToolUseId, string ToolName, string Input)> pendingToolUses
    )
    {
        var toolNames = pendingToolUses.Select(static tool => tool.ToolName).ToList();
        if (toolNames.All(static name => name == "read_file"))
        {
            return toolNames.Count == 1
                ? "I'm opening the most relevant file first."
                : $"I'm opening {toolNames.Count} relevant files to inspect the implementation.";
        }

        if (toolNames.All(static name => name == "glob_search"))
        {
            return "I'm locating candidate files first.";
        }

        if (toolNames.All(static name => name == "grep_search"))
        {
            return "I'm searching the codebase for the relevant symbols and placeholders first.";
        }

        return "I'm gathering the relevant code context first.";
    }

    private static string? NormalizeAssistantLiveText(string text)
    {
        var normalized = text.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static void ValidateToolUseInputs(IReadOnlyList<(string ToolUseId, string ToolName, string Input)> pendingToolUses)
    {
        foreach (var (_, toolName, input) in pendingToolUses)
        {
            try
            {
                JsonSerializer.Deserialize<JsonElement>(input);
            }
            catch (JsonException ex)
            {
                throw new RuntimeError($"assistant returned invalid JSON for tool `{toolName}`: {ex.Message}");
            }
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

    private sealed record PreparedToolExecution(
        int Index,
        Func<CancellationToken, Task<ConversationMessage>> ExecuteAsync
    )
    {
        public async Task<(int Index, ConversationMessage Message)> RunAsync(CancellationToken cancellationToken) =>
            (Index, await ExecuteAsync(cancellationToken));
    }
}
