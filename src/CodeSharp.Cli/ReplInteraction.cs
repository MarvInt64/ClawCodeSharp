using System.Text;
using CodeSharp.Core;

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

    // Input history
    private readonly List<string> _history = [];
    private int _historyIndex = -1;   // -1 = not browsing
    private string _savedDraft = ""; // draft saved before entering history

    // Cursor position within _draft (0 = before first char, _draft.Length = after last)
    private int _cursorPos;

    // Paste regions: large pastes stored in _draft at their real position,
    // displayed as "[Pasted N lines]" labels instead of raw text.
    private readonly record struct PasteRegion(int Start, int Length, int Lines, int Chars);
    private readonly List<PasteRegion> _pasteRegions = [];
    private const int PasteThresholdChars = 100;

    // Token usage source for status bar display
    private Func<TokenUsage>? _getTokenUsage;

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

    public void SetTokenUsageSource(Func<TokenUsage> getUsage)
    {
        lock (_gate)
        {
            _getTokenUsage = getUsage;
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
            _cursorPos = 0;
            _pasteRegions.Clear();
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
                    _cursorPos = 0;
                    _pasteRegions.Clear();
                    ResetCompletionStateLocked();
                    ResetHistoryStateLocked();

                    if (!string.IsNullOrEmpty(submitted))
                    {
                        // Add to history, avoid duplicates at the top
                        if (_history.Count == 0 || _history[^1] != submitted)
                            _history.Add(submitted);
                    }

                    if (busy)
                    {
                        RenderBusyFrameLocked();
                    }

                    return string.IsNullOrEmpty(submitted) ? null : new PromptSubmission(submitted);
                }
                case ConsoleKey.Backspace:
                {
                    var prLeft = FindPasteRegionEndingAtLocked(_cursorPos);
                    if (prLeft is { } leftRegion)
                    {
                        _draft.Remove(leftRegion.Start, leftRegion.Length);
                        _cursorPos = leftRegion.Start;
                        _pasteRegions.Remove(leftRegion);
                        ShiftPasteRegionsAfterLocked(leftRegion.Start, -leftRegion.Length);
                    }
                    else if (_cursorPos > 0)
                    {
                        _draft.Remove(_cursorPos - 1, 1);
                        _cursorPos--;
                        ShiftPasteRegionsAfterLocked(_cursorPos, -1);
                    }
                    ResetCompletionStateLocked();
                    ResetHistoryStateLocked();
                    break;
                }
                case ConsoleKey.Delete:
                {
                    var prRight = FindPasteRegionStartingAtLocked(_cursorPos);
                    if (prRight is { } rightRegion)
                    {
                        _draft.Remove(rightRegion.Start, rightRegion.Length);
                        _pasteRegions.Remove(rightRegion);
                        ShiftPasteRegionsAfterLocked(_cursorPos, -rightRegion.Length);
                    }
                    else if (_cursorPos < _draft.Length)
                    {
                        _draft.Remove(_cursorPos, 1);
                        ShiftPasteRegionsAfterLocked(_cursorPos, -1);
                    }
                    ResetCompletionStateLocked();
                    ResetHistoryStateLocked();
                    break;
                }
                case ConsoleKey.J when (key.Modifiers & ConsoleModifiers.Control) != 0:
                    _draft.Insert(_cursorPos, '\n');
                    _cursorPos++;
                    ResetCompletionStateLocked();
                    ResetHistoryStateLocked();
                    break;
                case ConsoleKey.LeftArrow:
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                        _cursorPos = FindPreviousWordBoundaryLocked();
                    else if (_cursorPos > 0)
                    {
                        _cursorPos--;
                        // If we stepped into a paste region, jump to its start
                        if (FindPasteRegionContainingLocked(_cursorPos) is { } pr)
                            _cursorPos = pr.Start;
                    }
                    break;
                case ConsoleKey.RightArrow:
                    if ((key.Modifiers & ConsoleModifiers.Control) != 0)
                        _cursorPos = FindNextWordBoundaryLocked();
                    else if (_cursorPos < _draft.Length)
                    {
                        _cursorPos++;
                        // If we stepped into a paste region, jump to its end
                        if (FindPasteRegionContainingLocked(_cursorPos) is { } pr)
                            _cursorPos = pr.Start + pr.Length;
                    }
                    break;
                case ConsoleKey.Home:
                    _cursorPos = 0;
                    break;
                case ConsoleKey.End:
                    _cursorPos = _draft.Length;
                    break;
                case ConsoleKey.UpArrow:
                    if (_history.Count > 0)
                    {
                        if (_historyIndex == -1)
                        {
                            // Save current draft before entering history
                            _savedDraft = _draft.ToString();
                            _historyIndex = _history.Count - 1;
                        }
                        else if (_historyIndex > 0)
                        {
                            _historyIndex--;
                        }
                        _draft.Clear();
                        _draft.Append(_history[_historyIndex]);
                        _cursorPos = _draft.Length;
                        _pasteRegions.Clear();
                        ResetCompletionStateLocked();
                    }
                    break;
                case ConsoleKey.DownArrow:
                    if (_historyIndex != -1)
                    {
                        _historyIndex++;
                        if (_historyIndex >= _history.Count)
                        {
                            // Past the end — restore saved draft
                            _draft.Clear();
                            _draft.Append(_savedDraft);
                            ResetHistoryStateLocked();
                        }
                        else
                        {
                            _draft.Clear();
                            _draft.Append(_history[_historyIndex]);
                        }
                        _cursorPos = _draft.Length;
                        _pasteRegions.Clear();
                        ResetCompletionStateLocked();
                    }
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        _draft.Insert(_cursorPos, key.KeyChar);
                        _cursorPos++;
                        ResetCompletionStateLocked();
                        ResetHistoryStateLocked();
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
        _pasteRegions.Clear();
        _draft.Append(completion);
        if (appendSpace)
        {
            _draft.Append(' ');
        }
        _cursorPos = _draft.Length;
    }

    private void ResetCompletionStateLocked()
    {
        _completionMatches = [];
        _completionIndex = -1;
    }

    private void ResetHistoryStateLocked()
    {
        _historyIndex = -1;
        _savedDraft = "";
    }

    private PasteRegion? FindPasteRegionEndingAtLocked(int pos) =>
        _pasteRegions.FirstOrDefault(r => r.Start + r.Length == pos) is { Length: > 0 } r2 ? r2 : null;

    private PasteRegion? FindPasteRegionStartingAtLocked(int pos) =>
        _pasteRegions.FirstOrDefault(r => r.Start == pos) is { Length: > 0 } r2 ? r2 : null;

    private PasteRegion? FindPasteRegionContainingLocked(int pos) =>
        _pasteRegions.FirstOrDefault(r => pos > r.Start && pos < r.Start + r.Length) is { Length: > 0 } r2 ? r2 : null;

    private void ShiftPasteRegionsAfterLocked(int afterPos, int delta)
    {
        for (var i = 0; i < _pasteRegions.Count; i++)
        {
            if (_pasteRegions[i].Start >= afterPos)
                _pasteRegions[i] = _pasteRegions[i] with { Start = _pasteRegions[i].Start + delta };
        }
    }

    private int FindPreviousWordBoundaryLocked()
    {
        var pos = _cursorPos;
        while (pos > 0 && _draft[pos - 1] == ' ') pos--;
        while (pos > 0 && _draft[pos - 1] != ' ') pos--;
        return pos;
    }

    private int FindNextWordBoundaryLocked()
    {
        var pos = _cursorPos;
        while (pos < _draft.Length && _draft[pos] != ' ') pos++;
        while (pos < _draft.Length && _draft[pos] == ' ') pos++;
        return pos;
    }

    private IReadOnlyList<string> BuildPromptLinesLocked()
    {
        var displayDraft = BuildDisplayDraftLocked();
        var draftLines = displayDraft.Split('\n');
        var result = new List<string>(draftLines.Length);

        for (var i = 0; i < draftLines.Length; i++)
        {
            var lineText = draftLines[i];
            if (i == 0 && draftLines.Length == 1)
            {
                // Single-line: first line with prompt and completion hint
                var hint = BuildCompletionHintLocked();
                result.Add(string.IsNullOrEmpty(hint)
                    ? $"{_prompt}{lineText}"
                    : $"{_prompt}{lineText}{hint}");
            }
            else if (i == 0)
            {
                // First line of multiline
                result.Add($"{_prompt}{lineText}");
            }
            else
            {
                // Continuation lines: indent to match prompt width
                result.Add($"  {lineText}");
            }
        }

        return result;
    }

    private string BuildDisplayDraftLocked()
    {
        if (_pasteRegions.Count == 0) return _draft.ToString();

        var sb = new StringBuilder();
        var pos = 0;
        foreach (var region in _pasteRegions.OrderBy(r => r.Start))
        {
            if (region.Start > pos)
                sb.Append(_draft, pos, region.Start - pos);
            sb.Append(FormatPasteLabel(region));
            pos = region.Start + region.Length;
        }
        if (pos < _draft.Length)
            sb.Append(_draft, pos, _draft.Length - pos);
        return sb.ToString();
    }

    private int DisplayCursorPosLocked()
    {
        if (_pasteRegions.Count == 0) return _cursorPos;

        var displayPos = _cursorPos;
        foreach (var region in _pasteRegions.OrderBy(r => r.Start))
        {
            if (region.Start >= _cursorPos) break;
            var regionEnd = region.Start + region.Length;
            var overlap = Math.Min(regionEnd, _cursorPos) - region.Start;
            displayPos -= overlap;
            displayPos += FormatPasteLabel(region).Length;
        }
        return displayPos;
    }

    private static string FormatPasteLabel(PasteRegion region) =>
        region.Lines > 1
            ? $"[Pasted {region.Lines} lines]"
            : $"[Pasted {region.Chars} chars]";

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
        // Slash completion only available on single-line drafts with no paste blocks
        if (!draft.StartsWith("/", StringComparison.Ordinal) || draft.Contains('\n') || _pasteRegions.Count > 0)
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

    /// <summary>
    /// Clears the live busy frame, commits <paramref name="content"/> to terminal scroll
    /// history (so prior turns remain visible), then renders the idle prompt.
    /// </summary>
    public void LeaveBusyAndCommit(string? content)
    {
        lock (_gate)
        {
            ClearRenderedBlockLocked(_bodyRenderLines);
            _bodyRenderLines = 0;
            _busyVisible = false;
            _queuedPreview = [];
            _activityPreview = [];
            _contentLines = [];

            if (!string.IsNullOrEmpty(content))
                Console.WriteLine(content);

            RenderIdleLocked();
        }
    }

    /// <summary>
    /// Clears the current live area and commits <paramref name="content"/> to terminal
    /// scroll history without re-rendering. Call before <see cref="EnterBusy"/>.
    /// </summary>
    public void CommitToHistory(string content)
    {
        lock (_gate)
        {
            ClearRenderedBlockLocked(_bodyRenderLines);
            _bodyRenderLines = 0;
            _contentLines = [];

            if (!string.IsNullOrEmpty(content))
                Console.WriteLine(content);
            // Caller will invoke EnterBusy which renders next
        }
    }

    /// <summary>
    /// Inserts pasted text at the current cursor position. Large pastes are shown
    /// as a compact "[Pasted N lines]" label; small pastes are inserted inline.
    /// </summary>
    public void HandlePaste(string text, bool busy)
    {
        lock (_gate)
        {
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            var lineCount = normalized.Count(c => c == '\n') + 1;
            var charCount = normalized.Length;

            // Shift existing regions that start at or after cursor
            ShiftPasteRegionsAfterLocked(_cursorPos, normalized.Length);

            _draft.Insert(_cursorPos, normalized);

            if (charCount >= PasteThresholdChars || lineCount > 1)
            {
                // Large or multiline paste: track as a paste region (shown as label)
                _pasteRegions.Add(new PasteRegion(_cursorPos, normalized.Length, lineCount, charCount));
            }

            _cursorPos += normalized.Length;
            ResetCompletionStateLocked();
            ResetHistoryStateLocked();

            if (busy) RenderBusyFrameLocked();
            else RenderIdleLocked();
        }
    }

    private string BuildStatus(string label)
    {
        var frame = SpinnerFrames[_spinnerIndex % SpinnerFrames.Length];
        _spinnerIndex++;

        var parts = new List<string> { $"{frame} {label}", "ESC cancels" };
        if (_queuedPreview.Count > 0)
        {
            parts.Add(_queuedPreview.Count == 1 ? "1 queued" : $"{_queuedPreview.Count} queued");
        }

        var usage = _getTokenUsage?.Invoke();
        if (usage is not null && usage.TotalTokens > 0)
        {
            parts.Add(FormatTokenCount(usage.TotalTokens));
        }

        return string.Join(" · ", parts);
    }

    private static string FormatTokenCount(long tokens) =>
        tokens >= 1_000_000 ? $"{tokens / 1_000_000.0:F1}M tok"
        : tokens >= 1_000 ? $"{tokens / 1_000.0:F1}k tok"
        : $"{tokens} tok";

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
        lines.AddRange(BuildPromptLinesLocked());

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
        PositionCursorAfterRenderLocked();
    }

    private void PositionCursorAfterRenderLocked()
    {
        var displayDraft = BuildDisplayDraftLocked();
        var displayCursor = DisplayCursorPosLocked();
        var displayCursor2 = Math.Min(displayCursor, displayDraft.Length);
        var displayLines = displayDraft.Split('\n');

        // Find which row/col the display cursor is in
        var beforeCursor = displayDraft[..displayCursor2];
        var beforeLines = beforeCursor.Split('\n');
        var cursorRow = beforeLines.Length - 1;
        var cursorCol = beforeLines[^1].Length;
        var totalRows = displayLines.Length;

        // Move up from the bottom of the rendered prompt to the cursor's row
        var rowsAfterCursor = totalRows - 1 - cursorRow;
        if (rowsAfterCursor > 0)
            Console.Write($"\u001b[{rowsAfterCursor}A");

        // Move left to cursor column within that row
        var cursorLineInDisplay = displayLines[cursorRow];
        var charsAfterCursor = cursorLineInDisplay.Length - cursorCol;

        // Add hint chars (only shown on single-line, no-paste drafts)
        if (totalRows == 1 && _pasteRegions.Count == 0)
            charsAfterCursor += VisibleLength(BuildCompletionHintLocked());

        if (charsAfterCursor > 0)
            Console.Write($"\u001b[{charsAfterCursor}D");
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
        var usage = _getTokenUsage?.Invoke();
        if (usage is not null && usage.TotalTokens > 0 && _contentLines.Count == 0)
        {
            lines.Add(ConsoleUi.Muted($"  {FormatTokenCount(usage.TotalTokens)} used this session"));
        }
        lines.AddRange(BuildPromptLinesLocked());
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
