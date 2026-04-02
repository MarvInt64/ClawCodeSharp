using System.Text;
using Claw.Core;

namespace Claw.Cli;

internal static class ConsoleUi
{
    private const string Reset = "\u001b[0m";

    private static bool UseAnsi =>
        !Console.IsOutputRedirected &&
        !string.Equals(Environment.GetEnvironmentVariable("NO_COLOR"), "1", StringComparison.Ordinal) &&
        !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);

    public static string Prompt() => $"{Brand("ClawCode#")} {Muted("›")} ";

    public static string Brand(string text) => Paint(text, "1;38;5;45");

    public static string Accent(string text) => Paint(text, "38;5;45");

    public static string Muted(string text) => Paint(text, "2;37");

    public static string Success(string text) => Paint(text, "38;5;42");

    public static string Warning(string text) => Paint(text, "38;5;214");

    public static string Error(string text) => Paint(text, "38;5;203");

    public static string Panel(
        string title,
        IEnumerable<(string Label, string Value)> rows,
        string? footer = null
    )
    {
        var builder = new StringBuilder();
        builder.Append(Accent("╭─ ")).Append(Brand(title)).AppendLine();

        foreach (var (label, value) in rows)
        {
            builder
                .Append(Accent("│"))
                .Append(' ')
                .Append(Muted($"{label,-14}"))
                .Append(' ')
                .AppendLine(value);
        }

        builder.Append(Accent("╰─"));
        if (!string.IsNullOrWhiteSpace(footer))
        {
            builder.Append(' ').Append(Muted(footer));
        }

        return builder.ToString();
    }

    public static string List(
        string title,
        IEnumerable<(string Name, string Description)> items,
        string? footer = null
    )
    {
        var entries = items.ToList();
        var columnWidth = entries.Count == 0 ? 0 : entries.Max(item => item.Name.Length) + 2;
        var builder = new StringBuilder();
        builder.Append(Accent("╭─ ")).Append(Brand(title)).AppendLine();

        foreach (var (name, description) in entries)
        {
            builder
                .Append(Accent("│"))
                .Append(' ')
                .Append(name.PadRight(columnWidth))
                .Append(' ')
                .AppendLine(description);
        }

        builder.Append(Accent("╰─"));
        if (!string.IsNullOrWhiteSpace(footer))
        {
            builder.Append(' ').Append(Muted(footer));
        }

        return builder.ToString();
    }

    public static string MessageBlock(string title, string body, string? footer = null)
    {
        var normalizedBody = string.IsNullOrWhiteSpace(body) ? "(no output)" : body.TrimEnd();
        var builder = new StringBuilder();
        builder.Append(Accent("╭─ ")).Append(Brand(title)).AppendLine();

        foreach (var line in normalizedBody.Replace("\r\n", "\n").Split('\n'))
        {
            builder.Append(Accent("│")).Append(' ').AppendLine(line);
        }

        builder.Append(Accent("╰─"));
        if (!string.IsNullOrWhiteSpace(footer))
        {
            builder.Append(' ').Append(Muted(footer));
        }

        return builder.ToString();
    }

    public static string AssistantTurn(string text, TokenUsage usage, IReadOnlyList<string> toolNames, int iterations)
    {
        var footerParts = new List<string>
        {
            $"in {usage.InputTokens:N0} / out {usage.OutputTokens:N0}"
        };

        if (toolNames.Count > 0)
        {
            footerParts.Add(
                toolNames.Count == 1
                    ? $"tool {toolNames[0]}"
                    : $"{toolNames.Count} tools {string.Join(", ", toolNames.Take(3))}"
            );
        }

        footerParts.Add(iterations == 1 ? "1 pass" : $"{iterations} passes");

        return MessageBlock("assistant", text, string.Join(" · ", footerParts));
    }

    public static string ErrorBlock(string message) =>
        MessageBlock("error", message, "Use --help or /help to inspect the current surface.");

    public static ConsoleSpinner StartSpinner(string label) => new(label);

    private static string Paint(string text, string code) =>
        UseAnsi ? $"\u001b[{code}m{text}{Reset}" : text;
}

internal sealed class ConsoleSpinner : IDisposable
{
    private static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;
    private readonly object _gate = new();
    private readonly string _label;
    private int _lastWidth;
    private bool _finished;

    public ConsoleSpinner(string label)
    {
        _label = label;
        _task = Task.Run(SpinAsync);
    }

    public void Succeed(string label = "Done") => Finish($"● {label}");

    public void Fail(string label = "Failed") => Finish($"○ {label}");

    public void Dispose()
    {
        if (_finished)
        {
            _cts.Dispose();
            return;
        }

        _cts.Cancel();
        try
        {
            _task.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e =>
            e is TaskCanceledException or OperationCanceledException))
        {
        }

        lock (_gate)
        {
            if (_lastWidth > 0)
            {
                Console.Write('\r');
                Console.Write(new string(' ', _lastWidth));
                Console.Write('\r');
            }
        }

        _cts.Dispose();
    }

    private async Task SpinAsync()
    {
        var index = 0;
        while (!_cts.IsCancellationRequested)
        {
            WriteStatus($"{Frames[index % Frames.Length]} {_label}");
            index++;

            try
            {
                await Task.Delay(80, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void Finish(string text)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _cts.Cancel();
        try
        {
            _task.Wait();
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e =>
            e is TaskCanceledException or OperationCanceledException))
        {
        }

        lock (_gate)
        {
            Console.Write('\r');
            Console.Write(text.PadRight(_lastWidth));
            Console.WriteLine();
        }
    }

    private void WriteStatus(string text)
    {
        lock (_gate)
        {
            _lastWidth = Math.Max(_lastWidth, text.Length);
            Console.Write('\r');
            Console.Write(text.PadRight(_lastWidth));
        }
    }
}
