using System.Text;
using System.Text.Json;

namespace CodeSharp.Core;

public record ConversationRuntimeCheckpoint(int MessageCount, UsageTrackerSnapshot Usage);

public class ConversationRuntime
{
    private const int AutoVerifyTimeoutMs = 120_000;

    private static readonly HashSet<string> ParallelSafeTools = new(StringComparer.Ordinal)
    {
        "read_file",
        "glob_search",
        "grep_search",
        "find_symbol",
        "find_references"
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
    private readonly PermissionMode _permissionMode;
    private readonly AutoVerifyMode _autoVerifyMode;
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
        PermissionMode permissionMode,
        AutoVerifyMode autoVerifyMode = AutoVerifyMode.DangerOnly,
        int maxIterations = int.MaxValue
    )
    {
        _session = session;
        _apiClient = apiClient;
        _toolExecutor = toolExecutor;
        _permissionPolicy = permissionPolicy;
        _permissionMode = permissionMode;
        _autoVerifyMode = autoVerifyMode;
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
        var mutatedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var automaticVerificationCompleted = false;
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
                if (!automaticVerificationCompleted &&
                    await TryRunAutomaticVerificationAsync(mutatedPaths, prompter, activitySink, cancellationToken) is { } verificationMessage)
                {
                    automaticVerificationCompleted = true;
                    _session.AddMessage(verificationMessage);
                    continue;
                }

                break;
            }

            var iterationResults = await ExecutePendingToolUsesAsync(
                pendingToolUses,
                mutatedPaths,
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
        ISet<string> mutatedPaths,
        IPermissionPrompter? prompter,
        Action<RuntimeActivity>? activitySink,
        CancellationToken cancellationToken
    )
    {
        var results = new ConversationMessage[pendingToolUses.Count];
        var parallelBatch = new List<PreparedToolExecution>();

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

    private async Task<ConversationMessage?> TryRunAutomaticVerificationAsync(
        IReadOnlyCollection<string> mutatedPaths,
        IPermissionPrompter? prompter,
        Action<RuntimeActivity>? activitySink,
        CancellationToken cancellationToken
    )
    {
        if (!ShouldRunAutomaticVerification() || mutatedPaths.Count == 0)
        {
            return null;
        }

        var workingDirectory = Directory.GetCurrentDirectory();
        var plan = AutoVerifyPlanner.TryCreate(workingDirectory, mutatedPaths);
        if (plan is null)
        {
            return null;
        }

        var toolUseId = $"auto-verify-{Guid.NewGuid():N}";
        var activityInput = JsonSerializer.Serialize(new
        {
            command = plan.Command,
            strategy = plan.Strategy,
            mutatedPaths = plan.MutatedPaths.Select(DisplayPath).ToArray()
        });

        var bashInput = JsonSerializer.Serialize(new
        {
            command = plan.Command,
            description = plan.Description,
            timeout = AutoVerifyTimeoutMs
        });

        var permissionResult = ShouldPromptForAutomaticVerification()
            ? _permissionPolicy.Authorize("bash", bashInput, prompter)
            : PermissionResult.Allowed;

        if (permissionResult.Outcome != PermissionOutcome.Allow)
        {
            activitySink?.Invoke(new RuntimeActivity.ToolStarted(toolUseId, "auto_verify", activityInput));
            activitySink?.Invoke(new RuntimeActivity.ToolBlocked(
                toolUseId,
                "auto_verify",
                permissionResult.Reason ?? "Permission denied"
            ));

            return ConversationMessage.UserText(
                BuildAutoVerifySystemNote(
                    plan,
                    status: "skipped",
                    summary: permissionResult.Reason ?? "Permission denied",
                    exitCode: null
                )
            );
        }

        activitySink?.Invoke(new RuntimeActivity.ToolStarted(toolUseId, "auto_verify", activityInput));
        var (output, isError) = await ExecuteToolWithErrorHandlingAsync("bash", bashInput, cancellationToken);
        var (summary, exitCode, previewLines) = SummarizeShellVerification(output, isError);
        var activityOutput = JsonSerializer.Serialize(new
        {
            command = plan.Command,
            strategy = plan.Strategy,
            exitCode,
            status = isError ? "failed" : "passed",
            preview = previewLines,
            mutatedPaths = plan.MutatedPaths.Select(DisplayPath).ToArray()
        });
        activitySink?.Invoke(new RuntimeActivity.ToolFinished(toolUseId, "auto_verify", activityOutput, isError));

        return ConversationMessage.UserText(
            BuildAutoVerifySystemNote(
                plan,
                status: isError ? "failed" : "passed",
                summary,
                exitCode
            )
        );
    }

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

    private bool ShouldRunAutomaticVerification() =>
        _autoVerifyMode switch
        {
            AutoVerifyMode.Off => false,
            AutoVerifyMode.DangerOnly => _permissionMode == PermissionMode.DangerFullAccess,
            AutoVerifyMode.On => true,
            _ => false
        };

    private bool ShouldPromptForAutomaticVerification() =>
        _permissionMode != PermissionMode.DangerFullAccess || _autoVerifyMode == AutoVerifyMode.On;

    private static string BuildAutoVerifySystemNote(
        AutoVerifyPlan plan,
        string status,
        string summary,
        int? exitCode
    )
    {
        var builder = new StringBuilder();
        builder.AppendLine("[Runtime verification note]");
        builder.AppendLine(status == "skipped"
            ? "Automatic verification was skipped after file edits."
            : "Automatic verification ran after file edits.");
        builder.AppendLine($"Strategy: {plan.Strategy}");
        builder.AppendLine($"Command: {plan.Command}");
        builder.AppendLine($"Status: {status}");
        if (exitCode is not null)
        {
            builder.AppendLine($"Exit code: {exitCode}");
        }

        var mutatedPaths = plan.MutatedPaths
            .Select(DisplayPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        if (mutatedPaths.Count > 0)
        {
            builder.AppendLine($"Files: {string.Join(", ", mutatedPaths)}");
        }

        if (!string.IsNullOrWhiteSpace(summary))
        {
            builder.AppendLine("Summary:");
            builder.AppendLine(summary.Trim());
        }

        builder.AppendLine("Use this as diagnostic context, not as a new user request.");

        return builder.ToString().TrimEnd();
    }

    private static (string Summary, int? ExitCode, IReadOnlyList<string> PreviewLines) SummarizeShellVerification(
        string output,
        bool isError
    )
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(output);
            if (json.ValueKind != JsonValueKind.Object)
            {
                return (Clip(output, 1200), null, WrapPreviewLines(output));
            }

            var exitCode = json.TryGetProperty("exitCode", out var exitCodeValue) &&
                           exitCodeValue.ValueKind == JsonValueKind.Number &&
                           exitCodeValue.TryGetInt32(out var parsedExitCode)
                ? parsedExitCode
                : (int?)null;

            var summary = json.TryGetProperty("error", out var error) &&
                          error.ValueKind == JsonValueKind.String &&
                          !string.IsNullOrWhiteSpace(error.GetString())
                ? error.GetString() ?? string.Empty
                : FirstNonEmpty(
                    JsonString(json, "stderr"),
                    JsonString(json, "stdout"),
                    isError ? output : "Verification passed."
                );

            var preview = WrapPreviewLines(
                FirstNonEmpty(JsonString(json, "stderr"), JsonString(json, "stdout"), summary)
            );

            return (Clip(summary, 1200), exitCode, preview);
        }
        catch
        {
            return (Clip(output, 1200), null, WrapPreviewLines(output));
        }
    }

    private static string? JsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static IReadOnlyList<string> WrapPreviewLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        return normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(6)
            .Select(line => Clip(line, 160))
            .ToList();
    }

    private static string Clip(string text, int maxChars)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        return $"{normalized[..Math.Max(0, maxChars - 1)]}…";
    }

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

        if (LooksLikeTruncatedPlanPreview(singleLine))
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

    private static bool LooksLikeTruncatedPlanPreview(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var trimmed = text.TrimEnd();
        var lastChar = trimmed[^1];
        if (char.IsPunctuation(lastChar) && lastChar is not ',' and not ';')
        {
            return false;
        }

        var lastToken = trimmed
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?
            .Trim('"', '\'', ')', ']', '}', '.', ',', ';', ':', '!', '?');

        if (string.IsNullOrEmpty(lastToken))
        {
            return true;
        }

        return lastToken.Length <= 2;
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
