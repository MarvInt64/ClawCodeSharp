using System.Text;

namespace Claw.Cli;

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
    private readonly object _gate = new();
    private bool _busyVisible;
    private int _busyRenderLines;
    private int _spinnerIndex;
    private string _busyLabel = "Thinking";
    private IReadOnlyList<string> _queuedPreview = [];
    private IReadOnlyList<string> _activityPreview = [];

    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    public ReplConsole(string prompt)
    {
        _prompt = prompt;
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
            Console.Write($"\r\u001b[2K{_prompt}{_draft}");
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
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                {
                    var submitted = _draft.ToString().Trim();
                    _draft.Clear();

                    if (!busy)
                    {
                        Console.WriteLine();
                    }

                    if (busy)
                    {
                        RenderBusyFrameLocked();
                    }
                    else
                    {
                        RenderIdleLocked();
                    }

                    return string.IsNullOrEmpty(submitted) ? null : new PromptSubmission(submitted);
                }
                case ConsoleKey.Backspace:
                    if (_draft.Length > 0)
                    {
                        _draft.Length--;
                    }
                    break;
                case ConsoleKey.Escape:
                    _draft.Clear();
                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        _draft.Append(key.KeyChar);
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

            ClearBusyBlockLocked();
            _busyVisible = false;
            _busyRenderLines = 0;
            _queuedPreview = [];
            _activityPreview = [];
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

        var lines = new List<string> { BuildStatus(_busyLabel) };
        lines.AddRange(_activityPreview.Select(line => $"  {line}"));
        lines.AddRange(_queuedPreview.Select((message, index) => $"  {index + 1}. {message}"));
        lines.Add($"{_prompt}{_draft}");

        ClearBusyBlockLocked();
        WriteBlockLocked(lines);
        _busyRenderLines = lines.Count;
    }

    private void RenderIdleLocked()
    {
        Console.Write($"\r\u001b[2K{_prompt}{_draft}");
    }

    private void ClearBusyBlockLocked()
    {
        if (_busyRenderLines == 0)
        {
            Console.Write("\r\u001b[2K");
            return;
        }

        if (_busyRenderLines > 1)
        {
            Console.Write($"\u001b[{_busyRenderLines - 1}A");
        }

        for (var i = 0; i < _busyRenderLines; i++)
        {
            Console.Write("\r\u001b[2K");
            if (i < _busyRenderLines - 1)
            {
                Console.Write('\n');
            }
        }

        if (_busyRenderLines > 1)
        {
            Console.Write($"\u001b[{_busyRenderLines - 1}A");
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

    private static IReadOnlyList<string> SnapshotQueuedInputs(IEnumerable<string> queuedInputs) =>
        queuedInputs
            .Select(PreviewQueuedMessage)
            .ToList();

    private static IReadOnlyList<string> SnapshotActivityLines(IEnumerable<string>? activityLines) =>
        activityLines?.ToList() ?? [];

    private static string PreviewQueuedMessage(string input)
    {
        var singleLine = input.Replace("\r\n", " ").Replace('\n', ' ').Trim();
        const int maxLength = 72;
        if (singleLine.Length <= maxLength)
        {
            return singleLine;
        }

        return $"{singleLine[..(maxLength - 1)]}…";
    }
}
