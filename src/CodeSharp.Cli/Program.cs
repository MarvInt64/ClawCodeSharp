using CodeSharp.Api;
using CodeSharp.Api.Providers;
using CodeSharp.Commands;
using CodeSharp.Core;
using CodeSharp.Plugins;
using CodeSharp.Tools;
using System.Text.Json;

namespace CodeSharp.Cli;

internal sealed record TurnExecutionResult(TurnSummary? Summary, string? Error, bool Interrupted);
internal enum ActivityLineStatus
{
    Info,
    Running,
    Success,
    Error,
    Blocked
}

internal sealed record ActivityLine(
    string ToolUseId,
    string ToolName,
    string Description,
    ActivityLineStatus Status,
    string? Detail = null,
    IReadOnlyList<string>? DetailLines = null
);

internal sealed record ActiveTurn(
    Task<TurnExecutionResult> Task,
    CancellationTokenSource Cancellation,
    TurnActivityState Activity
);

internal sealed class TurnActivityState
{
    private readonly object _gate = new();
    private readonly List<ActivityLine> _lines = [];

    public void Record(RuntimeActivity activity)
    {
        lock (_gate)
        {
            switch (activity)
            {
                case RuntimeActivity.AssistantDraft draft:
                    UpsertAssistantDraftLine(draft.Text);
                    break;
                case RuntimeActivity.AssistantPlan plan:
                    CommitAssistantPlanLine(plan.Text);
                    break;
                case RuntimeActivity.ToolStarted started:
                    var startDescription = MarkRetryIfNeeded(
                        started.ToolName,
                        Program.DescribeToolStart(started.ToolName, started.Input)
                    );
                    _lines.Add(new ActivityLine(
                        started.ToolUseId,
                        started.ToolName,
                        startDescription,
                        ActivityLineStatus.Running
                    ));
                    break;
                case RuntimeActivity.ToolFinished finished:
                {
                    var index = FindLastRunningIndex(finished.ToolUseId);
                    if (index >= 0)
                    {
                        var existing = _lines[index];
                        var description = Program.DescribeToolFinish(
                            finished.ToolName,
                            finished.Output,
                            existing.Description,
                            finished.IsError
                        );
                        _lines[index] = existing with
                        {
                            Description = description,
                            Status = finished.IsError ? ActivityLineStatus.Error : ActivityLineStatus.Success,
                            DetailLines = Program.ExtractToolPreviewLines(finished.ToolName, finished.Output, finished.IsError)
                        };
                    }
                    else
                    {
                        _lines.Add(new ActivityLine(
                            finished.ToolUseId,
                            finished.ToolName,
                            Program.DescribeToolFinish(
                                finished.ToolName,
                                finished.Output,
                                finished.ToolName,
                                finished.IsError
                            ),
                            finished.IsError ? ActivityLineStatus.Error : ActivityLineStatus.Success,
                            DetailLines: Program.ExtractToolPreviewLines(finished.ToolName, finished.Output, finished.IsError)
                        ));
                    }
                    break;
                }
                case RuntimeActivity.ToolBlocked blocked:
                {
                    var index = FindLastRunningIndex(blocked.ToolUseId);
                    if (index >= 0)
                    {
                        var existing = _lines[index];
                        _lines[index] = existing with
                        {
                            Status = ActivityLineStatus.Blocked,
                            Detail = blocked.Reason
                        };
                    }
                    else
                    {
                        _lines.Add(new ActivityLine(
                            blocked.ToolUseId,
                            blocked.ToolName,
                            blocked.ToolName,
                            ActivityLineStatus.Blocked,
                            blocked.Reason
                        ));
                    }
                    break;
                }
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            var output = new List<string>();
            foreach (var line in _lines)
            {
                if (line.ToolName == "assistant_plan" && output.Count > 0 && output[^1].Length > 0)
                {
                    output.Add(string.Empty);
                }

                output.AddRange(Program.FormatActivityLines(line));
            }

            return output;
        }
    }

    public IReadOnlyList<string> PersistentTurnSnapshot()
    {
        lock (_gate)
        {
            var output = new List<string>();
            foreach (var line in _lines.Where(ShouldPersistAfterTurn))
            {
                if (output.Count > 0 && output[^1].Length > 0)
                {
                    output.Add(string.Empty);
                }

                output.AddRange(Program.FormatActivityLines(line));
            }

            return output;
        }
    }

    private static bool ShouldPersistAfterTurn(ActivityLine line) =>
        line.ToolName != "assistant_draft";

    private int FindLastRunningIndex(string toolUseId)
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].ToolUseId == toolUseId && _lines[i].Status == ActivityLineStatus.Running)
            {
                return i;
            }
        }

        return -1;
    }

    private void UpsertAssistantDraftLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].ToolName != "assistant_draft")
            {
                continue;
            }

            if (string.Equals(_lines[i].Description, text, StringComparison.Ordinal))
            {
                return;
            }

            _lines[i] = _lines[i] with { Description = text, Status = ActivityLineStatus.Info };
            return;
        }

        _lines.Add(new ActivityLine(
            "assistant-draft",
            "assistant_draft",
            text,
            ActivityLineStatus.Info
        ));
    }

    private void CommitAssistantPlanLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        RemoveAssistantDraftLine();

        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].ToolName != "assistant_plan")
            {
                continue;
            }

            if (string.Equals(_lines[i].Description, text, StringComparison.Ordinal))
            {
                return;
            }

            break;
        }

        _lines.Add(new ActivityLine(
            $"assistant-plan-{_lines.Count}",
            "assistant_plan",
            text,
            ActivityLineStatus.Info
        ));
    }

    private void RemoveAssistantDraftLine()
    {
        for (var i = _lines.Count - 1; i >= 0; i--)
        {
            if (_lines[i].ToolName == "assistant_draft")
            {
                _lines.RemoveAt(i);
                return;
            }
        }
    }

    private string MarkRetryIfNeeded(string toolName, string description)
    {
        var attempt = CountAttempts(toolName, description) + 1;
        if (attempt == 1)
        {
            return description;
        }

        var retryDescription = toolName switch
        {
            "read_file" when description.StartsWith("reading ", StringComparison.Ordinal) =>
                $"re-reading {description["reading ".Length..]}",
            "edit_file" when description.StartsWith("editing ", StringComparison.Ordinal) =>
                $"retrying edit of {description["editing ".Length..]}",
            "write_file" when description.StartsWith("writing ", StringComparison.Ordinal) =>
                $"retrying write of {description["writing ".Length..]}",
            "glob_search" or "grep_search" => $"re-running {description}",
            _ => $"retrying {description}"
        };

        return $"{retryDescription} (attempt {attempt})";
    }

    private int CountAttempts(string toolName, string description)
    {
        var key = CanonicalizeStartDescription(toolName, description);
        return _lines.Count(line =>
            line.ToolName == toolName &&
            string.Equals(CanonicalizeStartDescription(line.ToolName, line.Description), key, StringComparison.Ordinal));
    }

    private static string CanonicalizeStartDescription(string toolName, string description)
    {
        var normalized = description;
        var attemptIndex = normalized.LastIndexOf(" (attempt ", StringComparison.Ordinal);
        if (attemptIndex >= 0)
        {
            normalized = normalized[..attemptIndex];
        }

        return toolName switch
        {
            "read_file" when normalized.StartsWith("re-reading ", StringComparison.Ordinal) =>
                $"reading {normalized["re-reading ".Length..]}",
            "edit_file" when normalized.StartsWith("retrying edit of ", StringComparison.Ordinal) =>
                $"editing {normalized["retrying edit of ".Length..]}",
            "write_file" when normalized.StartsWith("retrying write of ", StringComparison.Ordinal) =>
                $"writing {normalized["retrying write of ".Length..]}",
            "glob_search" or "grep_search" when normalized.StartsWith("re-running ", StringComparison.Ordinal) =>
                normalized["re-running ".Length..],
            "bash" or "PowerShell" when normalized.StartsWith("retrying ", StringComparison.Ordinal) =>
                normalized["retrying ".Length..],
            _ => normalized
        };
    }
}

public static class Program
{
    private const string Version = "0.1.0";
    private static readonly TimeSpan ExitInterruptWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PasteSubmitGuardWindow = TimeSpan.FromMilliseconds(300);
    private static readonly HashSet<string> HiddenModelTools = new(StringComparer.Ordinal)
    {
        "REPL",
        "Agent",
        "Skill",
        "ToolSearch",
        "NotebookEdit",
        "Config"
    };
    
    public static async Task<int> Main(string[] args)
    {
        try
        {
            var parser = new ArgumentParser();
            var options = parser.Parse(args);
            
            return await RunAsync(options);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ConsoleUi.ErrorBlock(ex.Message));
            return 1;
        }
    }
    
    private static async Task<int> RunAsync(CliOptions options)
    {
        return options.Action switch
        {
            CliAction.Help => PrintHelp(),
            CliAction.Version => PrintVersion(),
            CliAction.Login => await RunLoginAsync(),
            CliAction.Logout => RunLogout(),
            CliAction.Config => RunConfig(options.Args),
            CliAction.Init => await RunInitAsync(),
            CliAction.SystemPrompt => PrintSystemPrompt(options),
            CliAction.Agents => PrintAgents(options.Args),
            CliAction.Skills => PrintSkills(options.Args),
            CliAction.Resume => await RunResumeAsync(options),
            CliAction.Prompt => await RunPromptAsync(options),
            CliAction.Repl => await RunReplAsync(options),
            CliAction.DumpManifests => DumpManifests(),
            _ => 0
        };
    }
    
    private static int PrintHelp()
    {
        Console.WriteLine(ConsoleUi.Panel(
            "CodeSharp",
            [
                ("Usage", "codesharp [options] [prompt]"),
                ("Modes", "Interactive REPL or one-shot prompt"),
                ("Default model", "moonshotai/kimi-k2.5"),
                ("Providers", "anthropic · nvidia · openai · xai")
            ],
            "Run plain `codesharp` for the interactive agent shell."
        ));
        Console.WriteLine();
        Console.WriteLine(ConsoleUi.List(
            "Commands",
            [
                ("login", "Sign in with OAuth"),
                ("logout", "Clear saved credentials"),
                ("config", "Manage global provider/model/API key defaults"),
                ("init", "Initialize .codesharp configuration"),
                ("system-prompt", "Print the generated system prompt"),
                ("agents", "List available agents"),
                ("skills", "List available skills")
            ],
            "Inside REPL, use /help for slash commands."
        ));
        Console.WriteLine();
        Console.WriteLine(ConsoleUi.List(
            "Options",
            [
                ("-p <prompt>", "Run a single prompt and exit"),
                ("--model <name>", "Select a model"),
                ("--provider <name>", "Force provider selection"),
                ("--permission-mode", "read-only · workspace-write · danger-full-access"),
                ("--allowedTools", "Restrict the tool surface"),
                ("--output <format>", "text or json"),
                ("--version, -V", "Show version"),
                ("--help, -h", "Show this help")
            ]
        ));
        Console.WriteLine();
        Console.WriteLine(ConsoleUi.List(
            "Examples",
            [
                ("codesharp \"Explain this codebase\"", "One-shot prompt using the default model"),
                ("codesharp --model sonnet \"Write a function\"", "Pick a model alias explicitly"),
                ("codesharp --provider nvidia -p \"What does this file do?\"", "Route through NVIDIA NIM"),
                ("codesharp", "Start the interactive REPL")
            ]
        ));
        return 0;
    }
    
    private static int PrintVersion()
    {
        Console.WriteLine($"{ConsoleUi.Brand("CodeSharp")} {ConsoleUi.Muted(Version)}");
        return 0;
    }
    
    private static Task<int> RunLoginAsync()
    {
        Console.WriteLine(ConsoleUi.MessageBlock(
            "login",
            "OAuth login is not implemented yet.\nUse `codesharp config` to save provider defaults and API keys.",
            "Anthropic, OpenAI, xAI, and NVIDIA keys can be stored in ~/.codesharp/settings.json."
        ));
        return Task.FromResult(0);
    }
    
    private static int RunLogout()
    {
        Console.WriteLine(ConsoleUi.MessageBlock("logout", "Logout is not implemented yet."));
        return 0;
    }

    private static int RunConfig(string? args)
    {
        var result = ConfigCommandProcessor.Process(args, new GlobalSettingsStore());
        Console.WriteLine(ConsoleUi.MessageBlock(result.Title, result.Body, result.Footer));
        return 0;
    }
    
    private static Task<int> RunInitAsync()
    {
        var cwd = Directory.GetCurrentDirectory();
        
        var codesharpDir = Path.Combine(cwd, ".codesharp");
        Directory.CreateDirectory(codesharpDir);
        
        var settingsPath = Path.Combine(codesharpDir, "settings.json");
        if (!File.Exists(settingsPath))
        {
            File.WriteAllText(settingsPath, @"{
  ""model"": ""claude-opus-4-6"",
  ""permissionMode"": ""workspace-write""
}");
        }
        
        var codesharpMdPath = Path.Combine(cwd, "CODESHARP.md");
        if (!File.Exists(codesharpMdPath))
        {
            File.WriteAllText(codesharpMdPath, @"# Project Instructions

This file contains instructions for CodeSharp about this project.

## Project Structure

Describe your project structure here.

## Coding Conventions

- List your coding conventions
- Include style guidelines
- Note any important patterns

## Notes

Add any additional context about the project.
");
        }
        
        Console.WriteLine(ConsoleUi.Panel(
            "init",
            [
                ("Directory", cwd),
                ("Created", ".codesharp/settings.json"),
                ("Created", "CODESHARP.md")
            ],
            "Edit CODESHARP.md to give the agent project-specific guidance."
        ));
        
        return Task.FromResult(0);
    }
    
    private static int PrintSystemPrompt(CliOptions options)
    {
        var cwd = options.Cwd ?? Directory.GetCurrentDirectory();
        var date = options.Date ?? ModelAliases.DefaultDate;
        var pluginManager = new PluginManager(cwd);
        pluginManager.LoadFromConfig(Path.Combine(cwd, ".codesharp", "settings.json"));
        var registry = new GlobalToolRegistry(pluginManager.AggregatedTools);

        var systemPrompt = BuildSystemPrompt(
            cwd,
            date,
            options.PermissionMode,
            registry,
            options.AllowedTools
        );

        Console.WriteLine(ConsoleUi.MessageBlock(
            "system prompt",
            string.Join("\n\n", systemPrompt)
        ));
        
        return 0;
    }
    
    private static int PrintAgents(string? args)
    {
        Console.WriteLine(ConsoleUi.List(
            "agents",
            [
                ("explore", "Fast agent for codebase exploration"),
                ("general", "General-purpose agent for complex tasks")
            ]
        ));
        return 0;
    }
    
    private static int PrintSkills(string? args)
    {
        Console.WriteLine(ConsoleUi.List(
            "skills",
            [
                ("brev-cli", "Manage GPU/CPU cloud instances"),
                ("find-skills", "Discover and install agent skills"),
                ("fix-jira", "Fix Jira tickets")
            ]
        ));
        return 0;
    }
    
    private static int DumpManifests()
    {
        Console.WriteLine("Manifest dumping not yet implemented.");
        return 0;
    }
    
    private static Task<int> RunResumeAsync(CliOptions options)
    {
        if (options.SessionPath is null)
        {
            Console.Error.WriteLine(ConsoleUi.ErrorBlock("Session path required for --resume."));
            return Task.FromResult(1);
        }

        Console.WriteLine(ConsoleUi.Panel(
            "resume",
            [("Session", options.SessionPath)],
            "Session restore is not fully implemented yet."
        ));
        return Task.FromResult(0);
    }
    
    private static async Task<int> RunPromptAsync(CliOptions options)
    {
        if (options.Prompt is null)
        {
            Console.Error.WriteLine(ConsoleUi.ErrorBlock("Prompt required."));
            return 1;
        }
        
        var cwd = Directory.GetCurrentDirectory();
        var sessionPath = Path.Combine(cwd, ".codesharp", "sessions", $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        var provider = ProviderAccessWorkflow.ResolveProviderKind(options.Model, options.Provider);
        
        var (runtime, _, _, _) = BuildRuntime(options, sessionPath);

        try
        {
            using var interrupts = new ConsoleInterruptRelay();
            using var cts = new CancellationTokenSource();
            using var spinner = ConsoleUi.StartSpinner($"Thinking with {FormatProvider(provider)} · {options.Model}");
            var activity = new TurnActivityState();

            var task = ExecuteTurnAsync(runtime, options.Prompt, options.Model, activity, null, cts.Token);
            while (!task.IsCompleted)
            {
                if (interrupts.ConsumeRequested() && !cts.IsCancellationRequested)
                {
                    cts.Cancel();
                }

                await Task.Delay(50);
            }

            var result = await task;
            if (result.Interrupted)
            {
                spinner.Fail("Canceled");
                Console.WriteLine();
                Console.WriteLine(ConsoleUi.Warning("Request canceled."));
                return 1;
            }

            if (result.Error is not null)
            {
                spinner.Fail("Failed");
                Console.Error.WriteLine();
                Console.Error.WriteLine(ConsoleUi.ErrorBlock(result.Error));
                return 1;
            }

            spinner.Succeed("Done");

            Console.WriteLine();
            Console.WriteLine(ConsoleUi.AssistantTurn(
                ExtractAssistantText(result.Summary!),
                result.Summary!.Usage,
                ExtractToolNames(result.Summary!),
                result.Summary!.Iterations
            ));
            
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(ConsoleUi.ErrorBlock(ex.Message));
            return 1;
        }
    }
    
    private static async Task<int> RunReplAsync(CliOptions options)
    {
        var cwd = Directory.GetCurrentDirectory();
        var sessionPath = Path.Combine(cwd, ".codesharp", "sessions", $"session-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);
        
        var (runtime, _, toolExecutor, apiClient) = BuildRuntime(options, sessionPath);
        var provider = ProviderAccessWorkflow.ResolveProviderKind(options.Model, options.Provider);

        var repl = new ReplSession(
            runtime,
            toolExecutor,
            apiClient,
            options.Model,
            FormatProvider(provider),
            options.PermissionMode,
            sessionPath
        );

        var prompt = ConsoleUi.Prompt();
        var console = new ReplConsole(
            prompt,
            repl.CompletionCandidates,
            repl.StartupBanner().Replace("\r\n", "\n").Split('\n')
        );
        console.SetTokenUsageSource(() => repl.Usage.CumulativeUsage());
        var busyLabel = $"Thinking with {FormatProvider(provider)} · {options.Model}";
        var queuedInputs = new Queue<string>();
        ActiveTurn? activeTurn = null;
        var exitRequested = false;
        // Tracks a short guard window so a trailing Enter from the paste stream is not treated as Submit.
        var suppressSubmitAfterPasteUntil = DateTime.MinValue;
        IPermissionPrompter? prompter = repl.PermissionMode != PermissionMode.DangerFullAccess
            ? new ReplPermissionPrompter(repl.PermissionMode)
            : null;

        using var interrupts = new ConsoleInterruptRelay();

        console.InitializeScreen();

        while (!exitRequested)
        {
            if (interrupts.ConsumeRequested())
            {
                exitRequested = await ConfirmExitAsync(console, activeTurn, busyLabel, queuedInputs);
                continue;
            }

            if (activeTurn is not null && activeTurn.Task.IsCompleted)
            {
                var completed = activeTurn;
                activeTurn = null;

                var result = await completed.Task;
                var persistentTurnLines = completed.Activity.PersistentTurnSnapshot();

                string committedContent;
                if (result.Interrupted)
                    committedContent = ConsoleUi.Warning("Request canceled.");
                else if (result.Error is not null)
                    committedContent = ConsoleUi.ErrorBlock(result.Error);
                else
                    committedContent = ConsoleUi.AssistantTurn(
                        ExtractAssistantText(result.Summary!),
                        result.Summary!.Usage,
                        ExtractToolNames(result.Summary!),
                        result.Summary!.Iterations
                    );

                if (persistentTurnLines.Count > 0)
                {
                    committedContent = $"{ConsoleUi.MessageBlock("activity", persistentTurnLines)}\n{committedContent}";
                }

                console.LeaveBusyAndCommit(committedContent);

                continue;
            }

            if (activeTurn is null && queuedInputs.Count > 0)
            {
                var queued = queuedInputs.Dequeue();
                exitRequested = await HandleSubmittedInputAsync(
                    queued,
                    repl,
                    runtime,
                    busyLabel,
                    queuedInputs,
                    console,
                    prompter,
                    turn => activeTurn = turn,
                    newLabel => busyLabel = newLabel
                );
                if (!exitRequested && activeTurn is null)
                {
                    console.RenderIdlePrompt();
                }
                continue;
            }

            if (TryReadConsoleKey(out var key))
            {
                // ESC: cancel active turn, or clear draft if no active turn
                if (key.Key == ConsoleKey.Escape)
                {
                    if (activeTurn is not null && !activeTurn.Cancellation.IsCancellationRequested)
                    {
                        activeTurn.Cancellation.Cancel();
                    }
                    else
                    {
                        console.ClearDraft();
                        console.RenderIdlePrompt();
                    }
                    continue;
                }

                // Paste detection: first printable char + more keys immediately available
                PromptSubmission? submission;
                if (!char.IsControl(key.KeyChar) && Console.KeyAvailable && !ReplPermissionPrompter.IsConsoleActive)
                {
                    var pasteBuffer = new System.Text.StringBuilder();
                    pasteBuffer.Append(key.KeyChar);
                    while (Console.KeyAvailable && !ReplPermissionPrompter.IsConsoleActive)
                    {
                        var next = Console.ReadKey(intercept: true);
                        if (next.Key == ConsoleKey.Enter)
                            pasteBuffer.Append('\n');
                        else if (!char.IsControl(next.KeyChar))
                            pasteBuffer.Append(next.KeyChar);
                        else
                            break; // stop at other control chars
                    }

                    if (pasteBuffer.Length > 1)
                    {
                        console.HandlePaste(pasteBuffer.ToString(), activeTurn is not null);
                        suppressSubmitAfterPasteUntil = DateTime.UtcNow + PasteSubmitGuardWindow;
                        submission = null;
                    }
                    else
                    {
                        submission = console.HandleKey(key, activeTurn is not null);
                    }
                }
                else
                {
                    submission = console.HandleKey(key, activeTurn is not null);
                }

                // Guard: swallow the next Enter that arrives immediately after a paste.
                if (key.Key == ConsoleKey.Enter && DateTime.UtcNow <= suppressSubmitAfterPasteUntil)
                {
                    submission = null;
                    suppressSubmitAfterPasteUntil = DateTime.MinValue;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    suppressSubmitAfterPasteUntil = DateTime.MinValue;
                }

                if (submission is { } queuedSubmission)
                {
                    if (activeTurn is not null)
                    {
                        queuedInputs.Enqueue(queuedSubmission.Text);
                        console.TickBusy(busyLabel, queuedInputs, activeTurn.Activity.Snapshot());
                    }
                    else
                    {
                        exitRequested = await HandleSubmittedInputAsync(
                            queuedSubmission.Text,
                            repl,
                            runtime,
                            busyLabel,
                            queuedInputs,
                            console,
                            prompter,
                            turn => activeTurn = turn,
                            newLabel => busyLabel = newLabel
                        );
                        if (!exitRequested && activeTurn is null)
                        {
                            console.RenderIdlePrompt();
                        }
                    }
                }

                if (activeTurn is not null)
                {
                    console.TickBusy(busyLabel, queuedInputs, activeTurn.Activity.Snapshot());
                }

                continue;
            }

            if (activeTurn is not null)
            {
                console.TickBusy(busyLabel, queuedInputs, activeTurn.Activity.Snapshot());
                await Task.Delay(80);
            }
            else
            {
                await Task.Delay(25);
            }
        }

        if (activeTurn is not null && !activeTurn.Cancellation.IsCancellationRequested)
        {
            activeTurn.Cancellation.Cancel();
            await activeTurn.Task;
        }

        Console.WriteLine(ConsoleUi.Muted("Leaving REPL."));
        
        return 0;
    }

    private static string ExtractAssistantText(TurnSummary summary) =>
        summary.AssistantMessages
            .Select(message => string.Concat(
                message.Blocks
                    .OfType<ContentBlock.Text>()
                    .Select(text => text.Content)
            ))
            .LastOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? string.Empty;

    private static IReadOnlyList<string> ExtractToolNames(TurnSummary summary) =>
        summary.ToolResults
            .SelectMany(m => m.Blocks.OfType<ContentBlock.ToolResult>())
            .Select(t => t.ToolName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string FormatProvider(ProviderKind provider) => provider switch
    {
        ProviderKind.CodeSharpApi => "anthropic",
        ProviderKind.OpenAi => "openai",
        ProviderKind.Xai => "xai",
        ProviderKind.Nvidia => "nvidia",
        _ => provider.ToString().ToLowerInvariant()
    };

    internal static IReadOnlyList<string> FormatActivityLines(ActivityLine line)
    {
        var lines = new List<string>
        {
            line.Status switch
            {
                ActivityLineStatus.Info => ConsoleUi.AssistantPlan(line.Description),
                ActivityLineStatus.Running => $"⋯ {line.Description}",
                ActivityLineStatus.Success => $"{ConsoleUi.Success("✓")} {line.Description}",
                ActivityLineStatus.Error => $"{ConsoleUi.Error("✗")} {line.Description}",
                ActivityLineStatus.Blocked => $"! {line.Description} ({line.Detail ?? "blocked"})",
                _ => line.Description
            }
        };

        if (line.DetailLines is not null)
        {
            lines.AddRange(line.DetailLines.Select(FormatActivityDetailLine));
        }

        return lines;
    }

    private static string FormatActivityDetailLine(string line)
    {
        if (line.StartsWith("@@", StringComparison.Ordinal))
        {
            return $"    {ConsoleUi.DiffHunk(line)}";
        }

        if (line.StartsWith("+", StringComparison.Ordinal))
        {
            return $"    {ConsoleUi.DiffAdded(line)}";
        }

        if (line.StartsWith("-", StringComparison.Ordinal))
        {
            return $"    {ConsoleUi.DiffRemoved(line)}";
        }

        return $"    {ConsoleUi.Muted(line)}";
    }

    internal static string DescribeToolStart(string toolName, string input)
    {
        JsonElement? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(input);
        }
        catch
        {
        }

        return toolName switch
        {
            "read_file" => $"reading {DisplayPath(JsonString(payload, "path")) ?? "file"}",
            "write_file" => $"writing {DisplayPath(JsonString(payload, "path")) ?? "file"}",
            "edit_file" => $"editing {DisplayPath(JsonString(payload, "path")) ?? "file"}",
            "auto_verify" => DescribeAutoVerifyStart(payload),
            "glob_search" => DescribeGlobSearchStart(payload),
            "grep_search" => DescribeGrepSearchStart(payload),
            "find_symbol" => $"finding symbol {Quote(JsonString(payload, "symbol"))}",
            "find_references" => $"finding references to {Quote(JsonString(payload, "symbol"))}",
            "bash" => DescribeShellCommandStart(JsonString(payload, "command")),
            "PowerShell" => DescribeShellCommandStart(JsonString(payload, "command")),
            "WebFetch" => $"fetching {JsonString(payload, "url") ?? "url"}",
            "WebSearch" => $"searching web for {Quote(JsonString(payload, "query") ?? JsonString(payload, "prompt"))}",
            "TodoWrite" => DescribeTodoWrite(payload),
            _ => payload is not null
                ? $"{toolName} {Truncate(payload.Value.ToString(), 56)}"
                : toolName
        };
    }

    internal static string DescribeToolFinish(string toolName, string output, string fallbackDescription, bool isError)
    {
        JsonElement? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(output);
        }
        catch
        {
        }

        var path = DisplayPath(JsonString(payload, "path")) ?? ExtractPathFromActivityDescription(fallbackDescription) ?? "file";

        return toolName switch
        {
            "read_file" => DescribeReadFileFinish(payload, fallbackDescription),
            "write_file" => isError ? $"failed writing {path}" : DescribeDiffFinish("wrote", path, payload),
            "edit_file" => isError ? $"failed editing {path}" : DescribeDiffFinish("edited", path, payload),
            "auto_verify" => DescribeAutoVerifyFinish(payload, fallbackDescription, isError),
            "find_symbol" => DescribeSearchSummaryFinish(payload, fallbackDescription, "totalMatches"),
            "find_references" => DescribeSearchSummaryFinish(payload, fallbackDescription, "totalReferences"),
            "bash" => DescribeShellCommandFinish(payload, fallbackDescription, isError),
            "PowerShell" => DescribeShellCommandFinish(payload, fallbackDescription, isError),
            "TodoWrite" => isError ? "failed updating plan" : fallbackDescription,
            _ => fallbackDescription
        };
    }

    internal static IReadOnlyList<string>? ExtractToolPreviewLines(string toolName, string output, bool isError)
    {
        JsonElement? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<JsonElement>(output);
        }
        catch
        {
            return string.IsNullOrWhiteSpace(output)
                ? null
                : WrapToolDetailText(output);
        }

        if (toolName == "auto_verify")
        {
            return ExtractAutoVerifyPreviewLines(payload) ??
                   (string.IsNullOrWhiteSpace(output) ? null : WrapToolDetailText(output));
        }

        if (isError)
        {
            return string.IsNullOrWhiteSpace(output)
                ? null
                : WrapToolDetailText(output);
        }

        if (toolName == "read_file")
        {
            return ExtractReadFilePreviewLines(payload);
        }

        if (toolName is "find_symbol" or "find_references")
        {
            return ExtractSearchPreviewLines(payload);
        }

        if (toolName is "bash" or "PowerShell")
        {
            return ExtractShellPreviewLines(payload);
        }

        if (toolName is not ("write_file" or "edit_file"))
        {
            return null;
        }

        if (payload is not { ValueKind: JsonValueKind.Object } json ||
            !json.TryGetProperty("preview", out var preview) ||
            preview.ValueKind != JsonValueKind.Array)
        {
            return string.IsNullOrWhiteSpace(output)
                ? null
                : WrapToolDetailText(output);
        }

        var lines = preview.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString() ?? string.Empty)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        if (json.TryGetProperty("previewTruncated", out var truncated) &&
            truncated.ValueKind == JsonValueKind.True)
        {
            lines.Add("… diff preview truncated");
        }

        return lines.Count == 0 ? null : lines;
    }

    private static string? ExtractPathFromActivityDescription(string description)
    {
        var prefixes = new[]
        {
            "editing ",
            "writing ",
            "reading ",
            "re-reading ",
            "retrying edit of ",
            "retrying write of ",
            "failed editing ",
            "failed writing "
        };
        foreach (var prefix in prefixes)
        {
            if (description.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var path = description[prefix.Length..];
                var attemptIndex = path.LastIndexOf(" (attempt ", StringComparison.Ordinal);
                return attemptIndex >= 0 ? path[..attemptIndex] : path;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> WrapToolDetailText(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        const int maxLength = 160;
        return normalized
            .Split('\n')
            .Select(line => line.Length <= maxLength ? line : $"{line[..(maxLength - 1)]}…")
            .ToList();
    }

    private static string DescribeShellCommandFinish(JsonElement? payload, string fallbackDescription, bool isError)
    {
        var exitCode = JsonInt(payload, "exitCode");
        if (isError)
        {
            return exitCode is { } code
                ? $"{fallbackDescription} failed (exit {code})"
                : $"{fallbackDescription} failed";
        }

        return exitCode is { } successCode && successCode != 0
            ? $"{fallbackDescription} finished (exit {successCode})"
            : fallbackDescription;
    }

    private static string DescribeAutoVerifyStart(JsonElement? payload)
    {
        var strategy = JsonString(payload, "strategy");
        var command = JsonString(payload, "command");
        if (!string.IsNullOrWhiteSpace(strategy) && !string.IsNullOrWhiteSpace(command))
        {
            return $"verifying changes with {strategy} ({command})";
        }

        if (!string.IsNullOrWhiteSpace(command))
        {
            return $"verifying changes with {command}";
        }

        return "verifying changes";
    }

    private static string DescribeAutoVerifyFinish(JsonElement? payload, string fallbackDescription, bool isError)
    {
        var strategy = JsonString(payload, "strategy");
        var status = JsonString(payload, "status");
        var exitCode = JsonInt(payload, "exitCode");
        var label = string.IsNullOrWhiteSpace(strategy) ? fallbackDescription : $"verified changes with {strategy}";

        if (string.Equals(status, "skipped", StringComparison.OrdinalIgnoreCase))
        {
            return $"{label} skipped";
        }

        if (isError)
        {
            return exitCode is { } code
                ? $"{label} failed (exit {code})"
                : $"{label} failed";
        }

        return $"{label} passed";
    }

    private static string DescribeDiffFinish(string verb, string path, JsonElement? payload)
    {
        var added = JsonInt(payload, "linesAdded");
        var removed = JsonInt(payload, "linesRemoved");
        return added is null && removed is null
            ? $"{verb} {path}"
            : $"{verb} {path} ({FormatSignedCount(added, '+')} {FormatSignedCount(removed, '-')})";
    }

    private static string FormatSignedCount(int? value, char prefix) =>
        value is null ? $"{prefix}0" : $"{prefix}{value.Value:N0}";

    private static string DescribeSearchSummaryFinish(JsonElement? payload, string fallbackDescription, string countProperty)
    {
        var count = JsonInt(payload, countProperty);
        return count is null ? fallbackDescription : $"{fallbackDescription} ({count:N0})";
    }

    private static IReadOnlyList<string>? ExtractSearchPreviewLines(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }

        if (json.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Array)
        {
            return matches.EnumerateArray()
                .Take(8)
                .Select(FormatSearchPreviewItem)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        if (json.TryGetProperty("references", out var references) && references.ValueKind == JsonValueKind.Array)
        {
            return references.EnumerateArray()
                .Take(8)
                .Select(FormatSearchPreviewItem)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        return null;
    }

    private static string FormatSearchPreviewItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        var file = JsonString(item, "file") ?? "file";
        var line = JsonInt(item, "line");
        var kind = JsonString(item, "kind");
        var name = JsonString(item, "name");
        var language = JsonString(item, "language");
        var context = JsonString(item, "context");

        var location = line is null ? DisplayPath(file) : $"{DisplayPath(file)}:{line}";
        var prefix = !string.IsNullOrWhiteSpace(kind) && !string.IsNullOrWhiteSpace(name)
            ? $"{(string.IsNullOrWhiteSpace(language) ? string.Empty : $"{language} ")}{kind} {name} · "
            : string.Empty;

        return $"{prefix}{location} · {Truncate(context ?? string.Empty, 96)}";
    }

    private static IReadOnlyList<string>? ExtractAutoVerifyPreviewLines(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }

        if (json.TryGetProperty("preview", out var preview) && preview.ValueKind == JsonValueKind.Array)
        {
            return preview.EnumerateArray()
                .Take(8)
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? string.Empty)
                .Where(static line => !string.IsNullOrWhiteSpace(line))
                .ToList();
        }

        return null;
    }

    private static IReadOnlyList<string>? ExtractShellPreviewLines(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }

        var lines = new List<string>();
        AppendShellStreamPreview(lines, json, "stdout", "stdout");
        AppendShellStreamPreview(lines, json, "stderr", "stderr");

        var exitCode = JsonInt(payload, "exitCode");
        if (exitCode is { } code)
        {
            lines.Add($"exit code: {code}");
        }

        return lines.Count == 0 ? null : lines;
    }

    private static void AppendShellStreamPreview(List<string> lines, JsonElement payload, string propertyName, string label)
    {
        if (!payload.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = value.GetString()?.Replace("\r\n", "\n").Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        const int maxLines = 12;
        const int maxLineLength = 160;

        var streamLines = text.Split('\n');
        lines.Add($"{label}:");
        foreach (var line in streamLines.Take(maxLines))
        {
            lines.Add(line.Length <= maxLineLength ? line : $"{line[..(maxLineLength - 1)]}…");
        }

        if (streamLines.Length > maxLines)
        {
            lines.Add($"... {streamLines.Length - maxLines} more {label} lines");
        }
    }

    private static string? JsonString(JsonElement? payload, string propertyName)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }

        return json.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? JsonInt(JsonElement? payload, string propertyName)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }

        return json.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static bool? JsonBool(JsonElement? payload, string propertyName)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json)
        {
            return null;
        }

        return json.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : null;
    }

    private static string DescribeReadFileFinish(JsonElement? payload, string fallbackDescription)
    {
        var path = DisplayPath(JsonString(payload, "path")) ?? ExtractPathFromActivityDescription(fallbackDescription) ?? "file";
        var startLine = JsonInt(payload, "startLine");
        var endLine = JsonInt(payload, "endLine");

        if (startLine is > 0 && endLine is > 0)
        {
            return startLine == endLine
                ? $"read {path} (line {startLine})"
                : $"read {path} (lines {startLine}-{endLine})";
        }

        return $"read {path}";
    }

    private static IReadOnlyList<string>? ExtractReadFilePreviewLines(JsonElement? payload)
    {
        var hasMore = JsonBool(payload, "hasMore") == true;
        if (!hasMore)
        {
            return null;
        }

        var nextOffset = JsonInt(payload, "nextOffset");
        var nextLine = nextOffset is >= 0 ? nextOffset.Value + 1 : (int?)null;
        return
        [
            nextLine is > 0
                ? $"more available from line {nextLine}"
                : "more available"
        ];
    }

    private static string DescribeGlobSearchStart(JsonElement? payload)
    {
        var pattern = JsonString(payload, "pattern") ?? "files";
        var path = DisplayPath(JsonString(payload, "path")) ?? ".";

        if (pattern is "**/*" or "*")
        {
            return path == "." ? "scanning workspace files" : $"scanning files in {path}";
        }

        return $"finding {pattern} in {path}";
    }

    private static string DescribeGrepSearchStart(JsonElement? payload)
    {
        var pattern = Quote(JsonString(payload, "pattern"));
        var path = DisplayPath(JsonString(payload, "path")) ?? ".";
        return $"searching for {pattern} in {path}";
    }

    private static string DescribeShellCommandStart(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return "running shell command";
        }

        var trimmed = command.Trim();
        if (TryDescribeSedCommand(trimmed, out var sedDescription))
        {
            return sedDescription;
        }

        if (TryDescribeRipgrepCommand(trimmed, out var rgDescription))
        {
            return rgDescription;
        }

        if (TryDescribeHeadTailCommand(trimmed, out var headTailDescription))
        {
            return headTailDescription;
        }

        if (TryDescribeByteInspectionCommand(trimmed, out var byteDescription))
        {
            return byteDescription;
        }

        if (TryDescribeContentSearchCommand(trimmed, out var contentSearchDescription))
        {
            return contentSearchDescription;
        }

        return $"running {Quote(Truncate(trimmed, 120))}";
    }

    private static bool TryDescribeSedCommand(string command, out string description)
    {
        description = string.Empty;
        if (!command.StartsWith("sed -n ", StringComparison.Ordinal))
        {
            return false;
        }

        var path = ExtractPathLikeToken(command);
        if (string.IsNullOrWhiteSpace(path))
        {
            description = "inspecting exact file lines";
            return true;
        }

        description = $"inspecting exact lines in {DisplayPath(path) ?? path}";
        return true;
    }

    private static bool TryDescribeRipgrepCommand(string command, out string description)
    {
        description = string.Empty;
        if (!command.StartsWith("rg ", StringComparison.Ordinal))
        {
            return false;
        }

        description = $"running search command {Quote(Truncate(command, 120))}";
        return true;
    }

    private static bool TryDescribeHeadTailCommand(string command, out string description)
    {
        description = string.Empty;
        if (!command.Contains("head ", StringComparison.Ordinal) && !command.Contains("tail ", StringComparison.Ordinal))
        {
            return false;
        }

        var path = ExtractPathLikeToken(command);
        description = string.IsNullOrWhiteSpace(path)
            ? "inspecting a file excerpt"
            : $"inspecting a file excerpt in {DisplayPath(path) ?? path}";
        return true;
    }

    private static bool TryDescribeByteInspectionCommand(string command, out string description)
    {
        description = string.Empty;
        if (!command.StartsWith("od ", StringComparison.Ordinal))
        {
            return false;
        }

        var path = ExtractPathLikeToken(command);
        description = string.IsNullOrWhiteSpace(path)
            ? "inspecting file bytes"
            : $"inspecting file bytes in {DisplayPath(path) ?? path}";
        return true;
    }

    private static bool TryDescribeContentSearchCommand(string command, out string description)
    {
        description = string.Empty;
        if (!command.Contains("Get-Content", StringComparison.Ordinal) &&
            !command.Contains("Select-String", StringComparison.Ordinal))
        {
            return false;
        }

        var path = ExtractPathLikeToken(command);
        description = string.IsNullOrWhiteSpace(path)
            ? "inspecting file content via shell"
            : $"inspecting file content in {DisplayPath(path) ?? path}";
        return true;
    }

    private static string? ExtractPathLikeToken(string command)
    {
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var token = part.Trim().Trim('"', '\'', '(', ')');
            if (string.IsNullOrWhiteSpace(token) || token.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (LooksLikePath(token))
            {
                return token;
            }
        }

        return null;
    }

    private static bool LooksLikePath(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        return token.StartsWith("/", StringComparison.Ordinal) ||
               token.StartsWith("./", StringComparison.Ordinal) ||
               token.StartsWith("../", StringComparison.Ordinal) ||
               token.Contains('/') ||
               token.Contains('\\');
    }

    private static string? DisplayPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var normalized = path.Replace('\\', '/');
        var cwd = Directory.GetCurrentDirectory().Replace('\\', '/').TrimEnd('/');
        if (Path.IsPathRooted(path) &&
            normalized.StartsWith(cwd + "/", StringComparison.OrdinalIgnoreCase))
        {
            return normalized[(cwd.Length + 1)..];
        }

        return normalized;
    }

    private static string Quote(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "input" : $"`{value}`";

    private static string DescribeTodoWrite(JsonElement? payload)
    {
        if (payload is not { ValueKind: JsonValueKind.Object } json ||
            !json.TryGetProperty("todos", out var todos) ||
            todos.ValueKind != JsonValueKind.Array)
        {
            return "updating plan";
        }

        var todoItems = todos.EnumerateArray().ToList();
        var count = todoItems.Count;
        var active = todoItems
            .FirstOrDefault(todo =>
                todo.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                status.GetString() == "in_progress");

        var activeLabel =
            JsonString(active, "activeForm") ??
            JsonString(active, "content");

        if (!string.IsNullOrWhiteSpace(activeLabel))
        {
            return $"updating plan ({count} items, active: {Truncate(activeLabel, 40)})";
        }

        return count == 1 ? "updating plan (1 item)" : $"updating plan ({count} items)";
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : $"{value[..(maxLength - 1)]}…";
    }

    private static async Task<bool> HandleSubmittedInputAsync(
        string input,
        ReplSession repl,
        ConversationRuntime runtime,
        string busyLabel,
        Queue<string> queuedInputs,
        ReplConsole console,
        IPermissionPrompter? prompter,
        Action<ActiveTurn> setActiveTurn,
        Action<string>? onBusyLabelChanged = null
    )
    {
        var trimmed = input.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        if (trimmed is "/exit" or "/quit")
        {
            return true;
        }

        if (trimmed.StartsWith('/'))
        {
            var command = SlashCommand.Parse(trimmed);
            if (command is not null)
            {
                var result = await repl.HandleCommandAsync(command);
                if (result.NewBusyLabel is not null)
                    onBusyLabelChanged?.Invoke(result.NewBusyLabel);
                return false;
            }
        }

        var cts = new CancellationTokenSource();
        var activity = new TurnActivityState();
        console.CommitToHistory(ConsoleUi.UserTurn(trimmed));
        var task = ExecuteTurnAsync(runtime, trimmed, repl.Model, activity, prompter, cts.Token);
        setActiveTurn(new ActiveTurn(task, cts, activity));
        console.EnterBusy(busyLabel, queuedInputs, activity.Snapshot());
        return false;
    }

    private static async Task<TurnExecutionResult> ExecuteTurnAsync(
        ConversationRuntime runtime,
        string input,
        string model,
        TurnActivityState activity,
        IPermissionPrompter? prompter,
        CancellationToken cancellationToken
    )
    {
        var checkpoint = runtime.CaptureCheckpoint();

        if (TryPrecompactSession(runtime.Session, input, model, activity))
        {
            checkpoint = runtime.CaptureCheckpoint();
        }

        try
        {
            var summary = await runtime.RunTurnAsync(
                input,
                prompter: prompter,
                activitySink: activity.Record,
                cancellationToken: cancellationToken
            );
            return new TurnExecutionResult(summary, null, false);
        }
        catch (OperationCanceledException)
        {
            runtime.RestoreCheckpoint(checkpoint);
            return new TurnExecutionResult(null, null, true);
        }
        catch (RuntimeError ex) when (ex.Message == ConversationRuntime.UserInterruptMessage)
        {
            runtime.RestoreCheckpoint(checkpoint);
            return new TurnExecutionResult(null, null, true);
        }
        catch (RuntimeError ex)
        {
            return new TurnExecutionResult(null, ex.Message, false);
        }
        catch (ApiError ex) when (IsContextLimitError(ex))
        {
            runtime.RestoreCheckpoint(checkpoint);

            var compactedMessages = SessionCompactor.CompactToFitContext(
                runtime.Session.Messages,
                input,
                model,
                24_000
            );
            if (compactedMessages.SequenceEqual(runtime.Session.Messages))
            {
                return new TurnExecutionResult(null, BuildContextLimitError(ex), false);
            }

            ReplaceSession(runtime.Session, compactedMessages);
            activity.Record(new RuntimeActivity.AssistantPlan(
                "Session exceeded the model context window; retrying with compacted history."
            ));

            try
            {
                var summary = await runtime.RunTurnAsync(
                    input,
                    prompter: prompter,
                    activitySink: activity.Record,
                    cancellationToken: cancellationToken
                );
                return new TurnExecutionResult(summary, null, false);
            }
            catch (ApiError retryEx) when (IsContextLimitError(retryEx))
            {
                runtime.RestoreCheckpoint(checkpoint);
                return new TurnExecutionResult(null, BuildContextLimitError(retryEx), false);
            }
            catch (Exception retryError)
            {
                runtime.RestoreCheckpoint(checkpoint);
                return new TurnExecutionResult(null, retryError.Message, false);
            }
        }
        catch (ApiError ex)
        {
            runtime.RestoreCheckpoint(checkpoint);
            return new TurnExecutionResult(null, ex.Message, false);
        }
        catch (Exception ex)
        {
            runtime.RestoreCheckpoint(checkpoint);
            return new TurnExecutionResult(null, ex.Message, false);
        }
    }

    private static bool IsContextLimitError(ApiError error) =>
        error.Message.Contains("context_length_exceeded", StringComparison.OrdinalIgnoreCase) ||
        error.Message.Contains("context limit exceeded", StringComparison.OrdinalIgnoreCase) ||
        error.Message.Contains("maximum context length", StringComparison.OrdinalIgnoreCase);

    private static string BuildContextLimitError(ApiError error) =>
        $"{error.Message}\nUse /compact, /clear, or a narrower prompt if the session is still too large.";

    private static void ReplaceSession(Session session, IReadOnlyList<ConversationMessage> messages)
    {
        session.Clear();
        foreach (var message in messages)
        {
            session.AddMessage(message);
        }
    }

    private static bool TryPrecompactSession(
        Session session,
        string input,
        string model,
        TurnActivityState activity
    )
    {
        const int systemPromptReserveChars = 24_000;
        var currentMessages = session.Messages;
        var compactedMessages = SessionCompactor.CompactToFitContext(
            currentMessages,
            input,
            model,
            systemPromptReserveChars
        );
        if (compactedMessages.SequenceEqual(currentMessages))
        {
            return false;
        }

        ReplaceSession(session, compactedMessages);
        activity.Record(new RuntimeActivity.AssistantPlan(
            "Session grew large enough to risk the model context limit; compacting history before sending the next request."
        ));
        return true;
    }

    private static Task<bool> ConfirmExitAsync(
        ReplConsole console,
        ActiveTurn? activeTurn,
        string busyLabel,
        Queue<string> queuedInputs
    )
    {
        if (activeTurn is not null)
        {
            console.LeaveBusy();
        }
        
        console.SetContent(ConsoleUi.MessageBlock(
            "exit",
            "Quit CodeSharp?",
            "Press y to quit, any other key to stay."
        ));

        var key = Console.ReadKey(intercept: true);

        var confirmed = key.Key == ConsoleKey.Y;
        if (!confirmed)
        {
            if (activeTurn is not null)
            {
                console.EnterBusy(busyLabel, queuedInputs, activeTurn.Activity.Snapshot());
            }
            else
            {
                console.RenderIdlePrompt();
            }
        }

        return Task.FromResult(confirmed);
    }

    private static bool TryReadConsoleKey(out ConsoleKeyInfo key)
    {
        key = default;

        try
        {
            // Don't race with the permission prompter which reads keys on the turn thread
            if (!Console.KeyAvailable || ReplPermissionPrompter.IsConsoleActive)
            {
                return false;
            }

            key = Console.ReadKey(intercept: true);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
    
    private static (ConversationRuntime, GlobalToolRegistry, ToolExecutor, StreamingApiClient) BuildRuntime(CliOptions options, string sessionPath)
    {
        var cwd = Directory.GetCurrentDirectory();
        var settingsStore = new GlobalSettingsStore();
        var globalSettings = settingsStore.Load();
        
        var session = Session.New();
        
        var pluginManager = new PluginManager(cwd);
        pluginManager.LoadFromConfig(Path.Combine(cwd, ".codesharp", "settings.json"));
        
        var registry = new GlobalToolRegistry(pluginManager.AggregatedTools);
        
        var toolExecutor = new ToolExecutor(registry, cwd);
        
        var normalizedAllowedTools = options.AllowedTools is not null
            ? registry.NormalizeAllowedTools(options.AllowedTools)?.ToHashSet()
            : null;

        var permissionSpecs = registry.GetPermissionSpecs(
            normalizedAllowedTools
        ).Where(spec => IsModelVisibleTool(spec.Name)).ToList();
        
        var toolPermissions = permissionSpecs.ToDictionary(p => p.Name, p => p.Mode);
        var permissionPolicy = new PermissionPolicy(options.PermissionMode, toolPermissions);
        
        var provider = ProviderAccessWorkflow.ResolveProviderKind(options.Model, options.Provider);
        var resolution = ProviderAccessWorkflow.EnsureApiKeyAvailable(globalSettings, provider, options.Model);
        if (resolution.Prompted)
        {
            globalSettings = globalSettings.WithApiKey(provider, resolution.ApiKey);
            settingsStore.Save(globalSettings);
        }

        var providerClient = ProviderClient.FromProvider(provider, resolution.ApiKey);

        var tools = registry.GetDefinitions(normalizedAllowedTools)
            .Where(t => IsModelVisibleTool(t.Name))
            .Select(t => new Api.ToolDefinition(t.Name, t.Description, t.InputSchema))
            .ToList();

        var apiClient = new StreamingApiClient(providerClient, options.Model, tools);
        
        var systemPrompt = BuildSystemPrompt(
            cwd,
            DateTime.UtcNow.ToString("yyyy-MM-dd"),
            options.PermissionMode,
            registry,
            options.AllowedTools
        );
        
        var runtime = new ConversationRuntime(
            session,
            apiClient,
            toolExecutor,
            permissionPolicy,
            systemPrompt,
            options.PermissionMode,
            globalSettings.GetAutoVerifyMode()
        );
        
        return (runtime, registry, toolExecutor, apiClient);
    }

    private static IReadOnlyList<string> BuildSystemPrompt(
        string cwd,
        string date,
        PermissionMode permissionMode,
        GlobalToolRegistry registry,
        IReadOnlyList<string>? allowedTools
    )
    {
        var normalizedAllowedTools = allowedTools is not null
            ? registry.NormalizeAllowedTools(allowedTools)?.ToHashSet()
            : null;

        var toolSpecs = registry.GetAllTools()
            .Where(tool => normalizedAllowedTools is null || normalizedAllowedTools.Contains(tool.Name))
            .Where(tool => IsModelVisibleTool(tool.Name))
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToList();

        var toolLines = toolSpecs.Count == 0
            ? ["No tools are currently enabled."]
            : toolSpecs.Select(tool =>
                $"- {tool.Name}: {tool.Description} Requires {tool.RequiredPermission.AsString()} permission."
            ).ToList();

        var projectInstructions = ReadProjectInstructions(cwd);

        var prompt = new List<string>
        {
            "You are CodeSharp, an AI-powered code assistant.",
            $@"Environment
Date: {date}
Working Directory: {cwd}
Permission Mode: {permissionMode.AsString()}",
            $@"Available tools and constraints
{string.Join("\n", toolLines)}

Respect the tool surface exactly. Do not invent unavailable tools. Prefer read-only tools for analysis before using mutating tools. For write operations, make focused edits and avoid unrelated changes.",
            @"Tool usage guidance
- Before using tools, say in one concise sentence what you are about to inspect, search, or change.
- Use `find_symbol` when you know a likely identifier name and want declaration candidates across supported languages before falling back to broader regex search.
- Use `find_references` after locating a declaration to inspect likely impact before editing across the workspace.
- For repository exploration, start with grep_search or glob_search before calling read_file on many files.
- Use glob_search to narrow candidate files by name or path pattern.
- Use grep_search to find placeholders, TODOs, 'not implemented', specific symbols, or feature flags across the repo.
- grep_search results include the matched line, its line number, and surrounding context lines. Use those context lines to assess whether you need more — often they are enough to proceed without a read_file at all.
- When you do need read_file after grep_search, use the reported line number as the `offset` to jump directly to the relevant section. Never re-read a file from line 1 if you already know the target line number.
- Prefer smaller paged read_file calls over large full-file reads. `read_file` returns slices plus `hasMore` and `nextOffset` for follow-up reads.
- Avoid shotgun-reading many files just to discover where a feature lives.
- Avoid overly broad glob_search patterns like `**/*` unless you truly need the whole workspace.
- Prefer built-in file tools over ad-hoc shell inspection commands when `read_file`, `glob_search`, or `grep_search` already cover the need.
- If `edit_file` fails because the target snippet no longer matches, the error message will show the actual file content at that location. Use those exact lines as your new `old_string` and retry immediately — do not call `read_file` again unless the error says the line was not found at all.
- If `edit_file` fails a second time on the same location, stop retrying and use `write_file` to replace the entire function or section with a complete, correct version.
- After successful file edits, CodeSharp may run an automatic verification command in the background and feed you a compact system note with the result. Use that result instead of immediately re-running the same build command yourself unless you need a narrower follow-up check.
- Do not use `bash`, `PowerShell`, `head`, `tail`, `od`, `Get-Content`, or similar shell probes to inspect file contents when `read_file` can do it directly.
- Shell-based file inspection of workspace files may be rejected; use `read_file` instead.",
            @"Whole-project analysis
- When asked to review, analyze, or improve 'the project' or 'the codebase' broadly, do NOT dive deep into a single file. Instead, follow this order:
  1. Run glob_search('src/**/*.cs') (or the appropriate pattern) to enumerate all source files.
  2. Skim 4-8 representative files spread across different layers (entry points, core logic, data access, UI/CLI, tests) using small read_file slices (limit 40-80 lines each).
  3. Only after that broad scan, give a balanced assessment that references multiple files and layers — not just whichever file happened to be largest or first.
- A good broad review mentions: entry points, domain/business logic, infrastructure, error handling, tests, CI/CD, and security — spread across the actual files found.
- Do not let one large file dominate a broad analysis simply because it is large.",
            @"Search and verification rules
- Do not claim that something is absent from the codebase unless the relevant search result actually returned zero matches.
- For broad repo questions, prefer at least one broad search and one targeted follow-up search before concluding nothing was found.
- When reporting search results, mention what patterns you searched for and whether the result was truncated or sampled.
- If search finds matches, summarize the concrete files or categories instead of answering as if nothing was found.",
            @"Output style guidelines
- Be concise, direct, and technically precise.
- Prefer actionable answers over long preambles.
- Use Markdown when it improves readability.
- Use fenced code blocks with a language tag for code.
- Summarize findings clearly after inspection instead of dumping raw tool output.
- When giving file-specific feedback, mention the file and the concrete issue or recommendation.",
            @"Error-handling behavior
- If a tool fails, report the failure briefly and include the relevant reason.
- If another safe tool can recover the missing context, try that before giving up.
- If the task is blocked by permissions, missing files, or missing configuration, say so explicitly.
- Do not pretend a command, edit, or lookup succeeded if it did not.
- Preserve user work and avoid destructive actions unless explicitly requested."
        };

        if (!string.IsNullOrWhiteSpace(projectInstructions))
        {
            prompt.Add($"Project instructions\n{projectInstructions}");
        }

        return prompt;
    }

    private static bool IsModelVisibleTool(string toolName)
    {
        if (HiddenModelTools.Contains(toolName))
        {
            return false;
        }

        if (toolName == "PowerShell" && !OperatingSystem.IsWindows())
        {
            return false;
        }

        return true;
    }

    private static string? ReadProjectInstructions(string cwd)
    {
        var path = Path.Combine(cwd, "CODESHARP.md");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var content = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(content) ? null : content;
        }
        catch
        {
            return null;
        }
    }
}
