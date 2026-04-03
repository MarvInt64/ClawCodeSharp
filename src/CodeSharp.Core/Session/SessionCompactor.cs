using System.Text;
using System.Text.Json;

namespace CodeSharp.Core;

public static class SessionCompactor
{
    private const int DefaultKeepTailMessages = 8;
    private const int MaxSummaryChars = 12_000;
    private const int MaxTextSnippetChars = 320;
    private const int MaxToolSnippetChars = 220;
    private const double EstimatedCharsPerToken = 3.0;
    private const double CompactAtContextRatio = 0.55;
    private const int MinimumKeepTailMessages = 2;
    private static readonly int[] KeepTailCandidates = [8, 6, 4, 2];
    private static readonly TrimProfile[] TrimProfiles =
    [
        new(OlderTextChars: 1_200, RecentTextChars: 3_600, OlderToolInputChars: 900, RecentToolInputChars: 2_000, OlderToolOutputChars: 1_600, RecentToolOutputChars: 4_800, PreserveTailMessages: 4),
        new(OlderTextChars: 900, RecentTextChars: 2_400, OlderToolInputChars: 640, RecentToolInputChars: 1_400, OlderToolOutputChars: 1_000, RecentToolOutputChars: 2_400, PreserveTailMessages: 4),
        new(OlderTextChars: 640, RecentTextChars: 1_600, OlderToolInputChars: 480, RecentToolInputChars: 960, OlderToolOutputChars: 720, RecentToolOutputChars: 1_600, PreserveTailMessages: 3),
        new(OlderTextChars: 400, RecentTextChars: 1_000, OlderToolInputChars: 320, RecentToolInputChars: 640, OlderToolOutputChars: 480, RecentToolOutputChars: 1_000, PreserveTailMessages: 2)
    ];

    public static bool ShouldCompactForContext(
        IReadOnlyList<ConversationMessage> messages,
        string pendingUserInput,
        string model,
        int extraChars = 0
    )
    {
        var estimatedTokens = EstimateTokenCount(messages, pendingUserInput, extraChars);
        var budget = (int)(ModelAliases.EstimatedContextWindowForModel(model) * CompactAtContextRatio);
        return estimatedTokens >= budget;
    }

    public static IReadOnlyList<ConversationMessage> CompactToFitContext(
        IReadOnlyList<ConversationMessage> messages,
        string pendingUserInput,
        string model,
        int extraChars = 0
    )
    {
        var budget = (int)(ModelAliases.EstimatedContextWindowForModel(model) * CompactAtContextRatio);
        if (EstimateTokenCount(messages, pendingUserInput, extraChars) < budget)
        {
            return messages;
        }

        IReadOnlyList<ConversationMessage> bestCandidate = messages;
        foreach (var keepTailMessages in KeepTailCandidates)
        {
            var compactedHead = CompactHead(messages, keepTailMessages);
            if (EstimateTokenCount(compactedHead, pendingUserInput, extraChars) < budget)
            {
                return compactedHead;
            }

            var candidate = compactedHead;

            foreach (var profile in TrimProfiles)
            {
                candidate = TrimOversizedMessages(candidate, profile);
                if (EstimateTokenCount(candidate, pendingUserInput, extraChars) < budget)
                {
                    return candidate;
                }
            }

            bestCandidate = candidate;
        }

        return bestCandidate;
    }

    public static int EstimateTokenCount(
        IReadOnlyList<ConversationMessage> messages,
        string pendingUserInput,
        int extraChars = 0
    )
    {
        var totalChars = extraChars + pendingUserInput.Length;
        foreach (var message in messages)
        {
            totalChars += EstimateMessageChars(message);
        }

        return Math.Max(1, (int)Math.Ceiling(totalChars / (double)EstimatedCharsPerToken));
    }

    public static IReadOnlyList<ConversationMessage> CompactForContext(
        IReadOnlyList<ConversationMessage> messages,
        int keepTailMessages = DefaultKeepTailMessages
    ) => CompactHead(messages, keepTailMessages);

    private static IReadOnlyList<ConversationMessage> CompactHead(
        IReadOnlyList<ConversationMessage> messages,
        int keepTailMessages
    )
    {
        keepTailMessages = Math.Max(MinimumKeepTailMessages, keepTailMessages);
        if (messages.Count <= keepTailMessages + 2)
        {
            return messages.ToList();
        }

        var proposedHeadCount = Math.Max(0, messages.Count - keepTailMessages);
        var headCount = FindSafeHeadCount(messages, proposedHeadCount);
        if (headCount == 0)
        {
            return messages.ToList();
        }

        var earlierMessages = messages.Take(headCount).ToList();
        var tailMessages = messages.Skip(headCount).ToList();

        return
        [
            ConversationMessage.UserText(BuildSummary(earlierMessages)),
            .. tailMessages
        ];
    }

    private static int FindSafeHeadCount(
        IReadOnlyList<ConversationMessage> messages,
        int proposedHeadCount
    )
    {
        if (proposedHeadCount <= 0 || proposedHeadCount >= messages.Count)
        {
            return proposedHeadCount;
        }

        var start = proposedHeadCount;
        if (messages[start].Role != MessageRole.Tool)
        {
            return start;
        }

        while (start > 0 && messages[start].Role == MessageRole.Tool)
        {
            start--;
        }

        if (messages[start].Role == MessageRole.Assistant && HasToolUse(messages[start]))
        {
            return start;
        }

        while (start < messages.Count && messages[start].Role == MessageRole.Tool)
        {
            start++;
        }

        return start >= messages.Count ? 0 : start;
    }

    private static bool HasToolUse(ConversationMessage message) =>
        message.Blocks.OfType<ContentBlock.ToolUse>().Any();

    private static IReadOnlyList<ConversationMessage> TrimOversizedMessages(
        IReadOnlyList<ConversationMessage> messages,
        TrimProfile profile
    )
    {
        var result = new List<ConversationMessage>(messages.Count);
        var changed = false;

        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            var isRecent = index >= Math.Max(0, messages.Count - profile.PreserveTailMessages);
            var trimmedBlocks = new List<ContentBlock>(message.Blocks.Count);
            var messageChanged = false;

            foreach (var block in message.Blocks)
            {
                var trimmedBlock = block switch
                {
                    ContentBlock.Text text => TrimTextBlock(
                        text,
                        isRecent ? profile.RecentTextChars : profile.OlderTextChars,
                        message.Role
                    ),
                    ContentBlock.ToolUse toolUse => TrimToolUseBlock(
                        toolUse,
                        isRecent ? profile.RecentToolInputChars : profile.OlderToolInputChars
                    ),
                    ContentBlock.ToolResult toolResult => TrimToolResultBlock(
                        toolResult,
                        isRecent ? profile.RecentToolOutputChars : profile.OlderToolOutputChars
                    ),
                    _ => block
                };

                messageChanged |= !Equals(trimmedBlock, block);
                trimmedBlocks.Add(trimmedBlock);
            }

            if (messageChanged)
            {
                changed = true;
                result.Add(message with { Blocks = trimmedBlocks });
            }
            else
            {
                result.Add(message);
            }
        }

        return changed ? result : messages;
    }

    private static string BuildSummary(IReadOnlyList<ConversationMessage> messages)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[Earlier conversation compacted locally to fit the model context window]");
        sb.AppendLine($"Summarized {messages.Count} earlier messages.");

        for (var index = 0; index < messages.Count; index++)
        {
            var line = SummarizeMessage(messages[index]);
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var candidate = $"- {line}";
            if (sb.Length + candidate.Length + Environment.NewLine.Length > MaxSummaryChars)
            {
                var remaining = messages.Count - index;
                sb.AppendLine($"- ... {remaining} more earlier messages omitted");
                break;
            }

            sb.AppendLine(candidate);
        }

        return sb.ToString().TrimEnd();
    }

    private static string SummarizeMessage(ConversationMessage message)
    {
        var parts = new List<string>();
        foreach (var block in message.Blocks)
        {
            switch (block)
            {
                case ContentBlock.Text text when !string.IsNullOrWhiteSpace(text.Content):
                    parts.Add(Clip(CollapseWhitespace(text.Content), MaxTextSnippetChars));
                    break;

                case ContentBlock.ToolUse toolUse:
                    parts.Add($"called {toolUse.Name}{FormatToolInput(toolUse.Input)}");
                    break;

                case ContentBlock.ToolResult toolResult:
                    parts.Add($"tool {toolResult.ToolName}: {SummarizeToolResult(toolResult)}");
                    break;
            }
        }

        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var role = message.Role switch
        {
            MessageRole.User => "User",
            MessageRole.Assistant => "Assistant",
            MessageRole.Tool => "Tool",
            MessageRole.System => "System",
            _ => "Message"
        };

        return $"{role}: {string.Join("; ", parts)}";
    }

    private static string FormatToolInput(string input)
    {
        if (!TryParseJsonObject(input, out var json))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        AppendIfPresent(parts, json, "path");
        AppendIfPresent(parts, json, "pattern");
        AppendIfPresent(parts, json, "glob");
        AppendIfPresent(parts, json, "query");
        AppendIfPresent(parts, json, "url");
        AppendIfPresent(parts, json, "command");
        AppendIfPresent(parts, json, "offset");
        AppendIfPresent(parts, json, "limit");

        return parts.Count == 0 ? string.Empty : $" ({string.Join(", ", parts)})";
    }

    private static string SummarizeToolResult(ContentBlock.ToolResult toolResult)
    {
        if (!TryParseJsonObject(toolResult.Output, out var json))
        {
            return Clip(CollapseWhitespace(toolResult.Output), MaxToolSnippetChars);
        }

        if (json.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
        {
            return $"error={Clip(CollapseWhitespace(error.GetString() ?? string.Empty), MaxToolSnippetChars)}";
        }

        var parts = new List<string>();

        switch (toolResult.ToolName)
        {
            case "read_file":
                AppendIfPresent(parts, json, "path");
                AppendLineRange(parts, json, "startLine", "endLine");
                AppendIfPresent(parts, json, "hasMore");
                AppendArrayCount(parts, json, "lines");
                break;

            case "glob_search":
                AppendIfPresent(parts, json, "pattern");
                AppendIfPresent(parts, json, "totalFiles");
                AppendIfPresent(parts, json, "truncated");
                AppendArrayCount(parts, json, "files");
                break;

            case "grep_search":
                AppendIfPresent(parts, json, "pattern");
                AppendIfPresent(parts, json, "totalMatches");
                AppendIfPresent(parts, json, "filesWithMatches");
                AppendIfPresent(parts, json, "truncated");
                AppendArrayCount(parts, json, "matches");
                break;

            case "bash":
            case "PowerShell":
                AppendIfPresent(parts, json, "exitCode");
                AppendSnippetIfPresent(parts, json, "stdout");
                AppendSnippetIfPresent(parts, json, "stderr");
                AppendIfPresent(parts, json, "stdoutTruncated");
                AppendIfPresent(parts, json, "stderrTruncated");
                break;

            default:
                AppendIfPresent(parts, json, "path");
                AppendIfPresent(parts, json, "status");
                AppendIfPresent(parts, json, "resultCount");
                AppendIfPresent(parts, json, "exitCode");
                AppendIfPresent(parts, json, "truncated");
                break;
        }

        if (parts.Count == 0)
        {
            return Clip(CollapseWhitespace(toolResult.Output), MaxToolSnippetChars);
        }

        return string.Join(", ", parts);
    }

    private static ContentBlock TrimTextBlock(ContentBlock.Text text, int maxChars, MessageRole role)
    {
        if (text.Content.Length <= maxChars)
        {
            return text;
        }

        var label = role switch
        {
            MessageRole.User => "[Earlier user text truncated for context]\n",
            MessageRole.Assistant => "[Earlier assistant text truncated for context]\n",
            MessageRole.System => "[Earlier system text truncated for context]\n",
            _ => "[Earlier text truncated for context]\n"
        };

        return new ContentBlock.Text($"{label}{Clip(text.Content, maxChars)}");
    }

    private static ContentBlock TrimToolUseBlock(ContentBlock.ToolUse toolUse, int maxChars)
    {
        if (toolUse.Input.Length <= maxChars)
        {
            return toolUse;
        }

        var summarizedInput = TryParseJsonObject(toolUse.Input, out var json)
            ? BuildToolJsonSummary(json)
            : CollapseWhitespace(toolUse.Input);

        return new ContentBlock.ToolUse(
            toolUse.Id,
            toolUse.Name,
            Clip(summarizedInput, maxChars)
        );
    }

    private static ContentBlock TrimToolResultBlock(ContentBlock.ToolResult toolResult, int maxChars)
    {
        if (toolResult.Output.Length <= maxChars)
        {
            return toolResult;
        }

        var summary = SummarizeToolResult(toolResult);
        var trimmed = $"[Earlier tool output truncated for context]\n{summary}";
        return new ContentBlock.ToolResult(
            toolResult.ToolUseId,
            toolResult.ToolName,
            Clip(trimmed, maxChars),
            toolResult.IsError
        );
    }

    private static string BuildToolJsonSummary(JsonElement json)
    {
        var parts = new List<string>();
        AppendIfPresent(parts, json, "path");
        AppendIfPresent(parts, json, "pattern");
        AppendIfPresent(parts, json, "glob");
        AppendIfPresent(parts, json, "query");
        AppendIfPresent(parts, json, "url");
        AppendIfPresent(parts, json, "command");
        AppendIfPresent(parts, json, "offset");
        AppendIfPresent(parts, json, "limit");
        AppendIfPresent(parts, json, "stdout");
        AppendIfPresent(parts, json, "stderr");

        return parts.Count == 0 ? CollapseWhitespace(json.GetRawText()) : string.Join(", ", parts);
    }

    private static void AppendLineRange(List<string> parts, JsonElement json, string startProperty, string endProperty)
    {
        if (!TryGetScalarValue(json, startProperty, out var start) ||
            !TryGetScalarValue(json, endProperty, out var end))
        {
            return;
        }

        parts.Add($"lines={start}-{end}");
    }

    private static void AppendArrayCount(List<string> parts, JsonElement json, string property)
    {
        if (!json.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        parts.Add($"{property}={value.GetArrayLength()}");
    }

    private static void AppendSnippetIfPresent(List<string> parts, JsonElement json, string property)
    {
        if (!json.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = CollapseWhitespace(value.GetString() ?? string.Empty);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        parts.Add($"{property}={Clip(text, 120)}");
    }

    private static void AppendIfPresent(List<string> parts, JsonElement json, string property)
    {
        if (!TryGetScalarValue(json, property, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        parts.Add($"{property}={Clip(value, 120)}");
    }

    private static bool TryGetScalarValue(JsonElement json, string property, out string value)
    {
        value = string.Empty;
        if (!json.TryGetProperty(property, out var element))
        {
            return false;
        }

        value = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => string.Empty,
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(value);
    }

    private static bool TryParseJsonObject(string input, out JsonElement json)
    {
        json = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(input);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            json = document.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CollapseWhitespace(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int EstimateMessageChars(ConversationMessage message)
    {
        var total = 96;
        foreach (var block in message.Blocks)
        {
            total += block switch
            {
                ContentBlock.Text text => (int)Math.Ceiling(text.Content.Length * 1.15),
                ContentBlock.ToolUse toolUse => (int)Math.Ceiling(toolUse.Name.Length + (toolUse.Input.Length * 1.35) + 96),
                ContentBlock.ToolResult toolResult => (int)Math.Ceiling(toolResult.ToolName.Length + (toolResult.Output.Length * 1.75) + 128),
                _ => 0
            };
        }

        return total;
    }

    private static string Clip(string value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxChars)
        {
            return value;
        }

        return $"{value[..(maxChars - 1)]}…";
    }

    private sealed record TrimProfile(
        int OlderTextChars,
        int RecentTextChars,
        int OlderToolInputChars,
        int RecentToolInputChars,
        int OlderToolOutputChars,
        int RecentToolOutputChars,
        int PreserveTailMessages
    );
}
