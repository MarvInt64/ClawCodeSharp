using System.Text;

namespace CodeSharp.Cli;

internal readonly record struct PromptSubmission(string Text);

internal sealed class ConsoleInterruptRelay : IDisposable
{
    private int _requested;

    public ConsoleInterruptRelay()
    {
        Console.CancelKeyPress += HandleCancelKeyPress;
    }

    public bool ConsumeRequested() => Interlocked.Exchange(ref _requested, 0) == 1;

    public void Dispose()
    {
        Console.CancelKeyPress -= HandleCancelKeyPress;
    }

    private void HandleCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        Interlocked.Exchange(ref _requested, 1);
    }
}

internal sealed class ReplConsole
{
    private readonly StringBuilder _draft = new();
    private readonly string _prompt;
    private readonly IReadOnlyList<string> _slashCommands;
    private readonly IReadOnlyList<string> _headerLines;
    private readonly object _gate = new();
    private bool _busyVisible;
    private int _bodyRenderLines;
    private int _spinnerIndex;
    private string _busyLabel = "Thinking";
    private IReadOnlyList<string> _contentLines = [];
    private IReadOnlyList<string> _queuedPreview = [];
    private IReadOnlyList<string> _activityPreview = [];
    private IReadOnlyList<string> _completionMatches = [];
    private int _completionIndex = -1;

    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public ReplConsole(
        string prompt,
        IEnumerable<string>? slashCommands = null,
        IEnumerable<string>? headerLines = null
    )
    {
        _prompt = prompt;
        _slashCommands = (slashCommands ?? []).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(static c => c, StringComparer.OrdinalIgnoreCase).ToList();
        _headerLines = NormalizeLines(headerLines);
    }

    public void InitializeScreen()
    {
        lock (_gate)
        {
            try
            {
                Console.Clear();
            }
            catch
            {
            }

            foreach (var line in _headerLines)
            {
                Console.WriteLine(line);
            }

            if (_headerLines.Count > 0)
            {
                Console.WriteLine();
            }

            _bodyRenderLines = 0;
            _busyVisible = false;
            RenderIdleLocked();
        }
    }

    public void SetContent(string? block)
    {
        lock (_gate)
        {
            _contentLines = NormalizeLines(block);
            if (_busyVisible)
            {
                RenderBusyFrameLocked();
            }
            else
            {
                RenderIdleLocked();
            }
        }
    }

    public bool HasDraft
    {
        get
        {
            lock (_gate)
            {
                return _draft.Length > 0;
            }
        }
    }

    public void RenderIdlePrompt()
    {
        lock (_gate)
        {
            _busyVisible = false;
            RenderIdleLocked();
        }
    }

    public bool ClearDraft()
    {
        lock (_gate)
        {
            if (_draft.Length == 0)
            {
                return false;
            }

            _draft.Clear();
            return true;
        }
    }

    public PromptSubmission? HandleKey(ConsoleKeyInfo key, bool busy)
    {
        lock (_gate)
        {
            if (key.Key == ConsoleKey.Tab || key.KeyChar == '\t')
            {
                ApplySlashCompletionLocked();
            }
            else
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    var draftText = _draft.ToString();
                    var submitted = draftText.Trim();

                    _draft.Clear();
                    ResetCompletionStateLocked();

                    if (busy)
                    {
                        RenderBusyFrameLocked();
                    }

                    return string.IsNullOrEmpty(submitted) ? null : new PromptSubmission(submitted);
                }
                case ConsoleKey.Backspace:
                    if (_draft.Length > 0)
                    {
                        _draft.Length--;
                    }
                    ResetCompletionStateLocked();
                    break;
                case ConsoleKey.Escape:
                    _draft.Clear();
                    ResetCompletionStateLocked();
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        _draft.Append(key.KeyChar);
                        ResetCompletionStateLocked();
                    }
                    break;
            }

            if (busy)
            {
                RenderBusyFrameLocked();
            }
            else
            {
                RenderIdleLocked();
            }

            return null;
        }
    }

    private void ApplySlashCompletionLocked()
    {
        var currentToken = GetCurrentSlashTokenLocked();
        if (currentToken is null)
        {
            ResetCompletionStateLocked();
            return;
        }

        var matches = FindSlashMatchesLocked(currentToken);
        if (matches.Count == 0)
        {
            ResetCompletionStateLocked();
            return;
        }

        if (_completionMatches.Count > 0 &&
            _completionMatches.Contains(currentToken, StringComparer.OrdinalIgnoreCase))
        {
            _completionIndex = (_completionIndex + 1) % _completionMatches.Count;
            ReplaceDraftWithCompletionLocked(_completionMatches[_completionIndex], appendSpace: _completionMatches.Count == 1);
            return;
        }

        var sharedPrefix = LongestCommonPrefix(matches);
        _completionMatches = matches;
        _completionIndex = 0;
        if (sharedPrefix.Length > currentToken.Length)
        {
            ReplaceDraftWithCompletionLocked(sharedPrefix, appendSpace: matches.Count == 1);
            return;
        }

        ReplaceDraftWithCompletionLocked(matches[0], appendSpace: matches.Count == 1);
    }

    private void ReplaceDraftWithCompletionLocked(string completion, bool appendSpace)
    {
        _draft.Clear();
        _draft.Append(completion);
        if (appendSpace)
        {
            _draft.Append(' ');
        }
    }

    private void ResetCompletionStateLocked()
    {
        _completionMatches = [];
        _completionIndex = -1;
    }

    private string BuildPromptLineLocked()
    {
        var draft = _draft.ToString();
        var hint = BuildCompletionHintLocked();
        return string.IsNullOrEmpty(hint)
            ? $"{_prompt}{draft}"
            : $"{_prompt}{draft}{hint}";
    }

    private string BuildCompletionHintLocked()
    {
        var currentToken = GetCurrentSlashTokenLocked();
        if (currentToken is null)
        {
            return string.Empty;
        }

        var matches = FindSlashMatchesLocked(currentToken);
        if (matches.Count == 0)
        {
            return string.Empty;
        }

        if (matches.Count == 1)
        {
            var match = matches[0];
            if (match.Length <= currentToken.Length)
            {
                return ConsoleUi.Muted(" ");
            }

            return ConsoleUi.Muted(match[currentToken.Length..]);
        }

        return string.Empty;
    }

    private List<string> FindSlashMatchesLocked(string currentToken) =>
        _slashCommands
            .Where(cmd => cmd.StartsWith(currentToken, StringComparison.OrdinalIgnoreCase))
            .ToList();

    private string? GetCurrentSlashTokenLocked()
    {
        var draft = _draft.ToString();
        if (!draft.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        var firstSpace = draft.IndexOf(' ');
        if (firstSpace >= 0)
        {
            return draft[..firstSpace];
        }

        return draft.TrimEnd();
    }

    private static string LongestCommonPrefix(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var prefix = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            var other = values[i];
            var length = 0;
            var maxLength = Math.Min(prefix.Length, other.Length);
            while (length < maxLength &&
                   char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(other[length]))
            {
                length++;
            }

            prefix = prefix[..length];
            if (prefix.Length == 0)
            {
                break;
            }
        }

        return prefix;
    }

    public void EnterBusy(
        string label,
        IEnumerable<string> queuedInputs,
        IEnumerable<string>? activityLines = null
    )
    {
        lock (_gate)
        {
            _spinnerIndex = 0;
            _busyLabel = label;
            if (!_busyVisible)
            {
                _busyVisible = true;
            }

            _queuedPreview = SnapshotQueuedInputs(queuedInputs);
            _activityPreview = SnapshotActivityLines(activityLines);
            RenderBusyFrameLocked();
        }
    }

    public void TickBusy(
        string label,
        IEnumerable<string> queuedInputs,
        IEnumerable<string>? activityLines = null
    )
    {
        lock (_gate)
        {
            if (!_busyVisible)
            {
                return;
            }

            _busyLabel = label;
            _queuedPreview = SnapshotQueuedInputs(queuedInputs);
            _activityPreview = SnapshotActivityLines(activityLines);
            RenderBusyFrameLocked();
        }
    }

    public void LeaveBusy()
    {
        lock (_gate)
        {
            if (!_busyVisible)
            {
                return;
            }

            _busyVisible = false;
            _queuedPreview = [];
            _activityPreview = [];
            RenderIdleLocked();
        }
    }

    private string BuildStatus(string label)
    {
        var frame = SpinnerFrames[_spinnerIndex % SpinnerFrames.Length];
        _spinnerIndex++;

        var parts = new List<string> { $"{frame} {label}", "Ctrl+C cancels" };
        if (_queuedPreview.Count > 0)
        {
            parts.Add(_queuedPreview.Count == 1 ? "1 queued" : $"{_queuedPreview.Count} queued");
        }

        return string.Join(" · ", parts);
    }

    private void RenderBusyFrameLocked()
    {
        if (!_busyVisible)
        {
            return;
        }

        var lines = BuildContentLinesLocked();
        var wrappedActivityLines = _activityPreview
            .SelectMany(activityLine => WrapDisplayLine(activityLine, "  ", "    "))
            .ToList();
        var queuedLines = BuildQueuedLinesLocked();
        var fixedLineCount = lines.Count + queuedLines.Count + 1;
        var availableActivityLines = Math.Max(0, GetAvailableBodyHeightLocked() - fixedLineCount);

        if (wrappedActivityLines.Count > availableActivityLines)
        {
            var hiddenCount = wrappedActivityLines.Count - availableActivityLines;
            if (availableActivityLines > 0)
            {
                lines.Add(ConsoleUi.Muted($"  ... {hiddenCount} earlier activity lines hidden"));
                availableActivityLines--;
            }

            lines.AddRange(wrappedActivityLines.TakeLast(availableActivityLines));
        }
        else
        {
            lines.AddRange(wrappedActivityLines);
        }
        lines.AddRange(queuedLines);
        if (_activityPreview.Count > 0 &&
            !_activityPreview.Any(IsRunningActivityLine) &&
            !_activityPreview.Any(IsAssistantInfoLine))
        {
            lines.Add(ConsoleUi.Muted(
                HasErrorActivityLine(_activityPreview)
                    ? "  assistant is revising after a tool error..."
                    : "  waiting for assistant response..."
            ));
        }
        lines.Add(BuildStatus(_busyLabel));
        lines.Add(BuildPromptLineLocked());

        RenderBodyLocked(lines);
    }

    private void RenderIdleLocked()
    {
        RenderBodyLocked(BuildIdleLinesLocked());
    }

    private void RenderBodyLocked(IReadOnlyList<string> lines)
    {
        ClearRenderedBlockLocked(_bodyRenderLines);
        WriteBlockLocked(lines);
        _bodyRenderLines = lines.Count;
    }

    private List<string> BuildContentLinesLocked()
    {
        var lines = new List<string>();
        if (_contentLines.Count > 0)
        {
            lines.AddRange(_contentLines);
            lines.Add(string.Empty);
        }

        return lines;
    }

    private IReadOnlyList<string> BuildQueuedLinesLocked()
    {
        if (_queuedPreview.Count == 0)
        {
            return [];
        }

        var lines = new List<string>
        {
            ConsoleUi.Muted($"  queued next: {_queuedPreview[0]}")
        };

        if (_queuedPreview.Count > 1)
        {
            lines.Add(ConsoleUi.Muted($"  +{_queuedPreview.Count - 1} more queued"));
        }

        return lines;
    }

    private int GetAvailableBodyHeightLocked()
    {
        try
        {
            var headerHeight = _headerLines.Count + (_headerLines.Count > 0 ? 1 : 0);
            return Math.Max(8, Console.WindowHeight - headerHeight);
        }
        catch
        {
            return 24;
        }
    }

    private static void ClearRenderedBlockLocked(int lineCount)
    {
        if (lineCount == 0)
        {
            Console.Write("\r\u001b[2K");
            return;
        }

        if (lineCount > 1)
        {
            Console.Write($"\u001b[{lineCount - 1}A");
        }

        for (var i = 0; i < lineCount; i++)
        {
            Console.Write("\r\u001b[2K");
            if (i < lineCount - 1)
            {
                Console.Write('\n');
            }
        }

        if (lineCount > 1)
        {
            Console.Write($"\u001b[{lineCount - 1}A");
        }
    }

    private static void WriteBlockLocked(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            Console.Write("\r\u001b[2K");
            Console.Write(lines[i]);
            if (i < lines.Count - 1)
            {
                Console.Write('\n');
            }
        }
    }

    private List<string> BuildIdleLinesLocked()
    {
        var lines = BuildContentLinesLocked();
        lines.AddRange(BuildSuggestionLinesLocked());
        lines.Add(BuildPromptLineLocked());
        return lines;
    }

    private IReadOnlyList<string> BuildSuggestionLinesLocked()
    {
        var currentToken = GetCurrentSlashTokenLocked();
        if (currentToken is null)
        {
            return [];
        }

        var matches = FindSlashMatchesLocked(currentToken);
        if (matches.Count == 0)
        {
            return [];
        }

        var lines = new List<string>();
        var visibleMatches = matches.Take(6).ToList();
        for (var i = 0; i < visibleMatches.Count; i++)
        {
            var prefix = i == 0 ? ConsoleUi.Accent("  › ") : "    ";
            lines.Add($"{prefix}{visibleMatches[i]}");
        }

        if (matches.Count > visibleMatches.Count)
        {
            lines.Add(ConsoleUi.Muted($"    +{matches.Count - visibleMatches.Count} more"));
        }

        return lines;
    }

    private static IReadOnlyList<string> SnapshotQueuedInputs(IEnumerable<string> queuedInputs) =>
        queuedInputs
            .Select(PreviewQueuedMessage)
            .ToList();

    private static IReadOnlyList<string> SnapshotActivityLines(IEnumerable<string>? activityLines) =>
        activityLines?.ToList() ?? [];

    private static bool IsRunningActivityLine(string line) =>
        line.TrimStart().StartsWith("⋯", StringComparison.Ordinal);

    private static bool IsAssistantInfoLine(string line) =>
        line.Contains("PLAN", StringComparison.Ordinal);

    private static bool HasErrorActivityLine(IEnumerable<string> lines) =>
        lines.Any(line => line.TrimStart().StartsWith("✗", StringComparison.Ordinal));

    private static string PreviewQueuedMessage(string input)
    {
        var singleLine = input.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        const int maxLength = 56;
        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return $"{singleLine[..(maxLength - 1)]}…";
    }

    private static IReadOnlyList<string> WrapDisplayLine(string text, string firstPrefix, string continuationPrefix)
    {
        var availableWidth = Math.Max(20, ConsoleUi.ContentWidth() - VisibleLength(firstPrefix));
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
        {
            return [$"{firstPrefix}{text}"];
        }

        var lines = new List<string>();
        var currentPrefix = firstPrefix;
        var current = new StringBuilder();

        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : $"{current} {word}";
            if (VisibleLength(candidate) <= availableWidth)
            {
                current.Clear();
                current.Append(candidate);
                continue;
            }

            if (current.Length == 0)
            {
                lines.Add($"{currentPrefix}{word}");
                currentPrefix = continuationPrefix;
                continue;
            }

            lines.Add($"{currentPrefix}{current}");
            current.Clear();
            current.Append(word);
            currentPrefix = continuationPrefix;
        }

        if (current.Length > 0)
        {
            lines.Add($"{currentPrefix}{current}");
        }

        return lines;
    }

    private static int VisibleLength(string text)
    {
        var length = 0;
        var inEscape = false;

        foreach (var ch in text)
        {
            if (inEscape)
            {
                if (ch == 'm')
                {
                    inEscape = false;
                }

                continue;
            }

            if (ch == '\u001b')
            {
                inEscape = true;
                continue;
            }

            length++;
        }

        return length;
    }

    private static IReadOnlyList<string> NormalizeLines(IEnumerable<string>? lines) =>
        lines?.SelectMany(static line => line.Replace("\r\n", "\n").Split('\n')).ToList() ?? [];

    private static IReadOnlyList<string> NormalizeLines(string? block) =>
        string.IsNullOrWhiteSpace(block)
            ? []
            : block.Replace("\r\n", "\n").Split('\n');
}
