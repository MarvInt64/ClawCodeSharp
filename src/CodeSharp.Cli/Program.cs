using CodeSharp.Api;
using CodeSharp.Commands;
using CodeSharp.Core;
using CodeSharp.Plugins;
using CodeSharp.Tools;
using System.Text.Json;

namespace CodeSharp.Cli;

internal sealed record TurnExecutionResult(TurnSummary? Summary, string? Error, bool Interrupted);
internal enum ActivityLineStatus
{
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
    string? Detail = null
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
                case RuntimeActivity.ToolStarted started:
                    _lines.Add(new ActivityLine(
                        started.ToolUseId,
                        started.ToolName,
                        Program.DescribeToolStart(started.ToolName, started.Input),
                        ActivityLineStatus.Running
                    ));
                    break;
                case RuntimeActivity.ToolFinished finished:
                {
                    var index = FindLastRunningIndex(finished.ToolUseId);
                    if (index >= 0)
                    {
                        var existing = _lines[index];
                        _lines[index] = existing with
                        {
                            Status = finished.IsError ? ActivityLineStatus.Error : ActivityLineStatus.Success
                        };
                    }
                    else
                    {
                        _lines.Add(new ActivityLine(
                            finished.ToolUseId,
                            finished.ToolName,
                            finished.ToolName,
                            finished.IsError ? ActivityLineStatus.Error : ActivityLineStatus.Success
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

            while (_lines.Count > 6)
            {
                _lines.RemoveAt(0);
            }
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_gate)
        {
            return _lines.Select(Program.FormatActivityLine).ToList();
        }
    }

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
}

public static class Program
{
    private const string Version = "0.1.0";
    private static readonly TimeSpan ExitInterruptWindow = TimeSpan.FromSeconds(2);
    
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
                ("Default model", "claude-opus-4-6"),
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
        var provider = options.Provider ?? ProviderClient.DetectProviderKind(options.Model);
        
        var (runtime, _, _) = BuildRuntime(options, sessionPath);

        try
        {
            using var interrupts = new ConsoleInterruptRelay();
            using var cts = new CancellationTokenSource();
            using var spinner = ConsoleUi.StartSpinner($"Thinking with {FormatProvider(provider)} · {options.Model}");
            var activity = new TurnActivityState();

            var task = ExecuteTurnAsync(runtime, options.Prompt, activity, cts.Token);
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
        
        var (runtime, _, _) = BuildRuntime(options, sessionPath);
        var provider = options.Provider ?? ProviderClient.DetectProviderKind(options.Model);
        
        var repl = new ReplSession(
            runtime,
            options.Model,
            FormatProvider(provider),
            options.PermissionMode,
            sessionPath
        );
        
        Console.WriteLine(repl.StartupBanner());
        Console.WriteLine();

        var prompt = ConsoleUi.Prompt();
        var console = new ReplConsole(prompt, repl.CompletionCandidates);
        var busyLabel = $"Thinking with {FormatProvider(provider)} · {options.Model}";
        var queuedInputs = new Queue<string>();
        ActiveTurn? activeTurn = null;
        var exitRequested = false;
        DateTimeOffset? lastInterruptAt = null;

        using var interrupts = new ConsoleInterruptRelay();

        console.RenderIdlePrompt();

        while (!exitRequested)
        {
            if (interrupts.ConsumeRequested())
            {
                var now = DateTimeOffset.UtcNow;
                var doubleInterrupt = lastInterruptAt is { } last && now - last <= ExitInterruptWindow;
                lastInterruptAt = now;

                if (doubleInterrupt)
                {
                    exitRequested = await ConfirmExitAsync(console, activeTurn, busyLabel, queuedInputs);
                    if (!exitRequested)
                    {
                        lastInterruptAt = null;
                    }
                    continue;
                }

                if (activeTurn is not null && !activeTurn.Cancellation.IsCancellationRequested)
                {
                    activeTurn.Cancellation.Cancel();
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine(ConsoleUi.Warning("Press Ctrl+C again to quit."));
                    if (activeTurn is not null)
                    {
                        console.EnterBusy(busyLabel, queuedInputs, activeTurn.Activity.Snapshot());
                    }
                    else
                    {
                        console.RenderIdlePrompt();
                    }
                }
            }

            if (activeTurn is not null && activeTurn.Task.IsCompleted)
            {
                var completed = activeTurn;
                activeTurn = null;

                var result = await completed.Task;
                console.LeaveBusy();

                if (result.Interrupted)
                {
                    Console.WriteLine(ConsoleUi.Warning("Request canceled."));
                    Console.WriteLine();
                }
                else if (result.Error is not null)
                {
                    Console.Error.WriteLine(ConsoleUi.ErrorBlock(result.Error));
                    Console.WriteLine();
                }
                else
                {
                    Console.WriteLine(ConsoleUi.AssistantTurn(
                        ExtractAssistantText(result.Summary!),
                        result.Summary!.Usage,
                        ExtractToolNames(result.Summary!),
                        result.Summary!.Iterations
                    ));
                    Console.WriteLine();
                }

                if (queuedInputs.Count == 0)
                {
                    console.RenderIdlePrompt();
                }

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
                    turn => activeTurn = turn
                );
                if (!exitRequested && activeTurn is null)
                {
                    console.RenderIdlePrompt();
                }
                continue;
            }

            if (TryReadConsoleKey(out var key))
            {
                lastInterruptAt = null;
                var submission = console.HandleKey(key, activeTurn is not null);
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
                            turn => activeTurn = turn
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

    internal static string FormatActivityLine(ActivityLine line) => line.Status switch
    {
        ActivityLineStatus.Running => $"⋯ {line.Description}",
        ActivityLineStatus.Success => $"{ConsoleUi.Success("✓")} {line.Description}",
        ActivityLineStatus.Error => $"{ConsoleUi.Error("✗")} {line.Description}",
        ActivityLineStatus.Blocked => $"! {line.Description} ({line.Detail ?? "blocked"})",
        _ => line.Description
    };

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
            "read_file" => $"reading {JsonString(payload, "path") ?? "file"}",
            "write_file" => $"writing {JsonString(payload, "path") ?? "file"}",
            "edit_file" => $"editing {JsonString(payload, "path") ?? "file"}",
            "glob_search" => $"finding {JsonString(payload, "pattern") ?? "files"} in {JsonString(payload, "path") ?? "."}",
            "grep_search" => $"searching for {Quote(JsonString(payload, "pattern"))} in {JsonString(payload, "path") ?? "."}",
            "bash" => $"running {Quote(Truncate(JsonString(payload, "command"), 56))}",
            "PowerShell" => $"running {Quote(Truncate(JsonString(payload, "command"), 56))}",
            "WebFetch" => $"fetching {JsonString(payload, "url") ?? "url"}",
            "WebSearch" => $"searching web for {Quote(JsonString(payload, "query") ?? JsonString(payload, "prompt"))}",
            "TodoWrite" => DescribeTodoWrite(payload),
            _ => payload is not null
                ? $"{toolName} {Truncate(payload.Value.ToString(), 56)}"
                : toolName
        };
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
        Action<ActiveTurn> setActiveTurn
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
                await repl.HandleCommandAsync(command);
                return false;
            }
        }

        var cts = new CancellationTokenSource();
        var activity = new TurnActivityState();
        var task = ExecuteTurnAsync(runtime, trimmed, activity, cts.Token);
        setActiveTurn(new ActiveTurn(task, cts, activity));
        console.EnterBusy(busyLabel, queuedInputs, activity.Snapshot());
        return false;
    }

    private static async Task<TurnExecutionResult> ExecuteTurnAsync(
        ConversationRuntime runtime,
        string input,
        TurnActivityState activity,
        CancellationToken cancellationToken
    )
    {
        var checkpoint = runtime.CaptureCheckpoint();

        try
        {
            var summary = await runtime.RunTurnAsync(
                input,
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
        else
        {
            Console.WriteLine();
        }

        Console.WriteLine(ConsoleUi.MessageBlock(
            "exit",
            "Quit CodeSharp?",
            "Press y to quit, any other key to stay."
        ));

        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

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
            if (!Console.KeyAvailable)
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
    
    private static (ConversationRuntime, GlobalToolRegistry, ToolExecutor) BuildRuntime(CliOptions options, string sessionPath)
    {
        var cwd = Directory.GetCurrentDirectory();
        var globalSettings = new GlobalSettingsStore().Load();
        
        var session = Session.New();
        
        var pluginManager = new PluginManager(cwd);
        pluginManager.LoadFromConfig(Path.Combine(cwd, ".codesharp", "settings.json"));
        
        var registry = new GlobalToolRegistry(pluginManager.AggregatedTools);
        
        var toolExecutor = new ToolExecutor(registry, cwd);
        
        var permissionSpecs = registry.GetPermissionSpecs(
            options.AllowedTools is not null
                ? registry.NormalizeAllowedTools(options.AllowedTools)?.ToHashSet()
                : null
        );
        
        var toolPermissions = permissionSpecs.ToDictionary(p => p.Name, p => p.Mode);
        var permissionPolicy = new PermissionPolicy(options.PermissionMode, toolPermissions);
        
        var provider = options.Provider ?? globalSettings.GetProviderKind() ?? ProviderClient.DetectProviderKind(options.Model);
        var providerClient = ProviderClient.FromProvider(provider, globalSettings.GetApiKey(provider));

        var tools = registry.GetDefinitions(
            options.AllowedTools is not null
                ? registry.NormalizeAllowedTools(options.AllowedTools)?.ToHashSet()
                : null
        ).Select(t => new Api.ToolDefinition(t.Name, t.Description, t.InputSchema)).ToList();

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
            systemPrompt
        );
        
        return (runtime, registry, toolExecutor);
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
