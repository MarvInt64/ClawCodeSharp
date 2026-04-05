using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodeSharp.Api;
using CodeSharp.Commands;
using CodeSharp.Core;
using CodeSharp.Tools;

namespace CodeSharp.Cli;

public record SlashCommandResult(bool Consumed, string? NewBusyLabel = null);

public class ReplSession
{
    private const int CompactKeepTailCount = 6;
    private const int DiffMaxLines = 200;

    private readonly ConversationRuntime _runtime;
    private string _model;
    private string _provider;
    private readonly PermissionMode _permissionMode;
    private readonly CommandRegistry _commandRegistry;
    private readonly UsageTracker _usageTracker;
    private readonly string _sessionPath;
    private readonly GlobalSettingsStore _globalSettingsStore;
    private readonly StreamingApiClient _apiClient;
    private readonly ToolExecutor _toolExecutor;

    public ReplSession(
        ConversationRuntime runtime,
        ToolExecutor toolExecutor,
        StreamingApiClient apiClient,
        string model,
        string provider,
        PermissionMode permissionMode,
        string sessionPath
    )
    {
        _runtime = runtime;
        _toolExecutor = toolExecutor;
        _apiClient = apiClient;
        _model = model;
        _provider = provider;
        _permissionMode = permissionMode;
        _commandRegistry = new CommandRegistry();
        _usageTracker = runtime.Usage;
        _sessionPath = sessionPath;
        _globalSettingsStore = new GlobalSettingsStore();
    }

    public ConversationRuntime Runtime => _runtime;
    public string Model => _model;
    public AgentExecutionMode ExecutionMode => _runtime.Mode;
    public PermissionMode PermissionMode => _permissionMode;
    public UsageTracker Usage => _usageTracker;
    public string SessionPath => _sessionPath;
    public IEnumerable<string> CompletionCandidates => _commandRegistry.GetCompletionCandidates();

    public string StartupBanner()
    {
        var cwd = Directory.GetCurrentDirectory();
        var workspaceName = Path.GetFileName(cwd) ?? "workspace";
        var branch = GetGitBranch();
        var workspace = branch is null ? workspaceName : $"{workspaceName} · {branch}";

        return ConsoleUi.Panel(
            "CodeSharp",
            [
                ("Workspace", workspace),
                ("Directory", cwd),
                ("Model", _model),
                ("Provider", _provider),
                ("Mode", _runtime.Mode.AsString()),
                ("Permissions", _permissionMode.AsString()),
                ("Session", Path.GetFileNameWithoutExtension(_sessionPath))
            ],
            "/help · /plan · /config · /status · type while thinking to queue · ESC cancels · Ctrl+C quits"
        );
    }

    public async Task<SlashCommandResult> HandleCommandAsync(SlashCommand command)
    {
        switch (command.Kind)
        {
            case SlashCommandKind.Help:
                Console.WriteLine(CommandHandlers.RenderHelp(_commandRegistry));
                return new SlashCommandResult(false);

            case SlashCommandKind.Status:
                Console.WriteLine(CommandHandlers.RenderStatus(
                    _model,
                    _runtime.Mode,
                    _permissionMode,
                    _runtime.Session.Messages.Count,
                    _usageTracker.Turns(),
                    _usageTracker.CumulativeUsage().TotalTokens,
                    GetGitBranch()
                ));
                return new SlashCommandResult(false);

            case SlashCommandKind.Cost:
                Console.WriteLine(CommandHandlers.RenderCost(_usageTracker.CumulativeUsage(), _usageTracker.Turns()));
                return new SlashCommandResult(false);

            case SlashCommandKind.Model when !string.IsNullOrWhiteSpace(command.Args):
            {
                var newBusyLabel = SwitchModel(command.Args.Trim());
                return new SlashCommandResult(false, newBusyLabel);
            }

            case SlashCommandKind.Model:
                Console.WriteLine(CommandHandlers.RenderModelReport(_model, _runtime.Session.Messages.Count, _usageTracker.Turns()));
                return new SlashCommandResult(false);

            case SlashCommandKind.Permissions:
                Console.WriteLine(CommandHandlers.RenderPermissionsReport(_permissionMode.AsString()));
                return new SlashCommandResult(false);

            case SlashCommandKind.Version:
                Console.WriteLine(CommandHandlers.RenderVersion("0.1.0"));
                return new SlashCommandResult(false);

            case SlashCommandKind.Export:
                await ExportSessionAsync(command.Path);
                return new SlashCommandResult(true);

            case SlashCommandKind.Clear when command.Confirm == true:
                _runtime.Session.Clear();
                Console.WriteLine(ConsoleUi.MessageBlock("session", "Session history cleared."));
                return new SlashCommandResult(true);

            case SlashCommandKind.Clear:
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "clear",
                    "Confirmation required.",
                    "Run /clear --confirm to remove the current session history."
                ));
                return new SlashCommandResult(false);

            case SlashCommandKind.Compact:
                await CompactSessionAsync();
                return new SlashCommandResult(false);

            case SlashCommandKind.Diff:
                await HandleDiffAsync();
                return new SlashCommandResult(false);

            case SlashCommandKind.Symbols:
                await HandleSymbolsAsync(command.Args);
                return new SlashCommandResult(false);

            case SlashCommandKind.References:
                await HandleReferencesAsync(command.Args);
                return new SlashCommandResult(false);

            case SlashCommandKind.Commit:
                await HandleCommitAsync();
                return new SlashCommandResult(false);

            case SlashCommandKind.Plan:
                return new SlashCommandResult(false, HandlePlanCommand(command.Args));

            case SlashCommandKind.Branch:
                await HandleBranchAsync(command.Args);
                return new SlashCommandResult(false);

            case SlashCommandKind.Worktree:
                await HandleWorktreeAsync(command.Args);
                return new SlashCommandResult(false);

            case SlashCommandKind.Pr:
                await HandlePrAsync(command.Args);
                return new SlashCommandResult(false);

            case SlashCommandKind.Init:
                Console.WriteLine(ConsoleUi.MessageBlock("init", "Init from inside REPL is not implemented yet."));
                return new SlashCommandResult(false);

            case SlashCommandKind.Config:
            {
                var previousSettings = _globalSettingsStore.Load();
                var headerLines = StartupBanner().Replace("\r\n", "\n").Split('\n');
                var result = ConfigCommandProcessor.Process(command.Section, _globalSettingsStore, headerLines);
                var updatedSettings = _globalSettingsStore.Load();
                string? newBusyLabel = null;
                var footer = string.IsNullOrWhiteSpace(command.Section)
                    ? "Global defaults apply to new CodeSharp sessions."
                    : result.Footer;

                if (updatedSettings != previousSettings)
                {
                    try
                    {
                        newBusyLabel = ApplyGlobalSettingsToCurrentSession(updatedSettings);
                        footer = string.IsNullOrWhiteSpace(footer)
                            ? "Applied to the current REPL session."
                            : $"{footer}\nApplied to the current REPL session.";
                    }
                    catch (InvalidOperationException ex)
                    {
                        Console.WriteLine(ConsoleUi.ErrorBlock(ex.Message));
                    }
                }

                Console.WriteLine(ConsoleUi.MessageBlock(result.Title, result.Body, footer));
                return new SlashCommandResult(false, newBusyLabel);
            }

            case SlashCommandKind.Memory:
                HandleMemoryCommand(command.Args);
                return new SlashCommandResult(false);

            case SlashCommandKind.Agents:
                Console.WriteLine(ConsoleUi.MessageBlock("agents", "Agent management in REPL is not implemented yet."));
                return new SlashCommandResult(false);

            case SlashCommandKind.Skills:
                Console.WriteLine(ConsoleUi.MessageBlock("skills", "Skill management in REPL is not implemented yet."));
                return new SlashCommandResult(false);

            case SlashCommandKind.Plugins:
                Console.WriteLine(ConsoleUi.MessageBlock("plugins", "Plugin management in REPL is not implemented yet."));
                return new SlashCommandResult(false);

            case SlashCommandKind.Unknown:
                Console.WriteLine(ConsoleUi.ErrorBlock(
                    $"Unknown command: /{command.Name}\nType /help for available commands."
                ));
                return new SlashCommandResult(false);

            default:
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "todo",
                    $"Command '{command.Kind}' is not implemented yet."
                ));
                return new SlashCommandResult(false);
        }
    }

    private string SwitchModel(string modelInput)
    {
        var resolvedModel = ProviderDetection.ResolveModelAlias(modelInput);
        var providerKind = ProviderAccessWorkflow.ResolveProviderKind(resolvedModel);
        var settings = _globalSettingsStore.Load();
        string apiKey;
        try
        {
            var resolution = ProviderAccessWorkflow.EnsureApiKeyAvailable(
                settings,
                providerKind,
                resolvedModel,
                StartupBanner().Replace("\r\n", "\n").Split('\n')
            );
            apiKey = resolution.ApiKey;
            if (resolution.Prompted)
            {
                _globalSettingsStore.Save(settings.WithApiKey(providerKind, resolution.ApiKey));
            }
        }
        catch (InvalidOperationException ex)
        {
            Console.WriteLine(ConsoleUi.ErrorBlock(ex.Message));
            return BuildBusyLabel();
        }

        var newClient = ProviderClient.FromProvider(providerKind, apiKey);

        _apiClient.SwitchModel(resolvedModel, newClient);
        _model = resolvedModel;
        _provider = FormatProvider(providerKind);

        Console.WriteLine(ConsoleUi.MessageBlock(
            "model",
            $"Switched to {_model}",
            $"Provider: {_provider}"
        ));
        return BuildBusyLabel();
    }

    private string HandlePlanCommand(string? args)
    {
        var action = args?.Trim();
        if (string.IsNullOrWhiteSpace(action))
        {
            _runtime.Mode = AgentExecutionMode.Planning;
            _runtime.PlanningDepth = null;
            Console.WriteLine(ConsoleUi.MessageBlock(
                "plan",
                "Planning mode enabled.\nThe assistant may inspect the workspace and produce a plan, but it cannot edit files or run mutating tools.",
                "Use /plan deep for a more exhaustive plan, /plan approve to switch back to execute mode, or /plan exit to leave planning mode."
            ));
            return BuildBusyLabel();
        }

        switch (action.ToLowerInvariant())
        {
            case "deep":
                _runtime.Mode = AgentExecutionMode.Planning;
                _runtime.PlanningDepth = "deep";
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "plan",
                    "Deep planning mode enabled.\nThe assistant should inspect more broadly, compare alternatives, and produce a more detailed execution plan.",
                    "Use /plan approve to switch back to execute mode when the plan looks right."
                ));
                return BuildBusyLabel();
            case "approve":
                _runtime.Mode = AgentExecutionMode.Execute;
                _runtime.PlanningDepth = null;
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "plan",
                    "Planning approved.\nSwitched back to execute mode.",
                    "Your next request can implement the approved plan."
                ));
                return BuildBusyLabel();
            case "exit":
            case "off":
                _runtime.Mode = AgentExecutionMode.Execute;
                _runtime.PlanningDepth = null;
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "plan",
                    "Planning mode disabled.\nSwitched back to execute mode."
                ));
                return BuildBusyLabel();
            case "status":
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "plan",
                    $"Current mode: {_runtime.Mode.AsString()}",
                    _runtime.Mode == AgentExecutionMode.Planning && string.Equals(_runtime.PlanningDepth, "deep", StringComparison.OrdinalIgnoreCase)
                        ? "Depth: deep"
                        : null
                ));
                return BuildBusyLabel();
            default:
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "plan",
                    "Usage:\n/plan\n/plan deep\n/plan approve\n/plan exit\n/plan status"
                ));
                return BuildBusyLabel();
        }
    }

    private string ApplyGlobalSettingsToCurrentSession(GlobalSettings settings)
    {
        var effectiveModel = settings.Model ?? _model;
        var effectiveProvider = ProviderAccessWorkflow.ResolveProviderKind(effectiveModel, settings.GetProviderKind());
        var resolution = ProviderAccessWorkflow.EnsureApiKeyAvailable(
            settings,
            effectiveProvider,
            effectiveModel,
            StartupBanner().Replace("\r\n", "\n").Split('\n')
        );
        if (resolution.Prompted)
        {
            _globalSettingsStore.Save(settings.WithApiKey(effectiveProvider, resolution.ApiKey));
        }

        var newClient = ProviderClient.FromProvider(effectiveProvider, resolution.ApiKey);

        _apiClient.SwitchModel(effectiveModel, newClient);
        _model = effectiveModel;
        _provider = FormatProvider(effectiveProvider);

        return BuildBusyLabel();
    }

    private string BuildBusyLabel()
    {
        var prefix = _runtime.Mode == AgentExecutionMode.Planning ? "Planning" : "Thinking";
        return $"{prefix} with {_provider} · {_model}";
    }

    private static string FormatProvider(ProviderKind provider) => provider switch
    {
        ProviderKind.CodeSharpApi => "anthropic",
        ProviderKind.OpenAi => "openai",
        ProviderKind.Xai => "xai",
        ProviderKind.Nvidia => "nvidia",
        _ => provider.ToString().ToLowerInvariant()
    };

    private async Task HandleSymbolsAsync(string? args)
    {
        var symbol = args?.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            Console.WriteLine(ConsoleUi.MessageBlock("symbols", "Usage: /symbols <name>"));
            return;
        }

        var input = JsonSerializer.Serialize(new { symbol, match_type = "contains", limit = 20 });
        var result = await _toolExecutor.ExecuteAsync("find_symbol", input);
        Console.WriteLine(RenderSymbolSearchResult(result.Output, symbol));
    }

    private async Task HandleReferencesAsync(string? args)
    {
        var symbol = args?.Trim();
        if (string.IsNullOrWhiteSpace(symbol))
        {
            Console.WriteLine(ConsoleUi.MessageBlock("refs", "Usage: /refs <name>"));
            return;
        }

        var input = JsonSerializer.Serialize(new { symbol, include_declarations = true, limit = 40 });
        var result = await _toolExecutor.ExecuteAsync("find_references", input);
        Console.WriteLine(RenderReferenceSearchResult(result.Output, symbol));
    }

    private static string RenderSymbolSearchResult(string output, string symbol)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var totalMatches = root.GetProperty("totalMatches").GetInt32();
            if (totalMatches == 0)
            {
                return ConsoleUi.MessageBlock("symbols", $"No symbol matches for `{symbol}`.");
            }

            var lines = new List<string>();
            foreach (var item in root.GetProperty("matches").EnumerateArray())
            {
                var language = JsonString(item, "language") ?? "source";
                var kind = JsonString(item, "kind") ?? "symbol";
                var name = JsonString(item, "name") ?? symbol;
                var file = JsonString(item, "file") ?? "file";
                var line = JsonInt(item, "line");
                var context = JsonString(item, "context");

                lines.Add($"{language} {kind} {name} · {file}:{line}");
                if (!string.IsNullOrWhiteSpace(context))
                {
                    lines.Add($"  {context}");
                }
            }

            var footer = $"{totalMatches} match{(totalMatches == 1 ? string.Empty : "es")}";
            if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            {
                footer += " · truncated";
            }

            return ConsoleUi.MessageBlock("symbols", lines, footer);
        }
        catch
        {
            return ConsoleUi.ErrorBlock(output);
        }
    }

    private static string RenderReferenceSearchResult(string output, string symbol)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var totalReferences = root.GetProperty("totalReferences").GetInt32();
            if (totalReferences == 0)
            {
                return ConsoleUi.MessageBlock("refs", $"No references found for `{symbol}`.");
            }

            var lines = new List<string>();
            foreach (var item in root.GetProperty("references").EnumerateArray())
            {
                var file = JsonString(item, "file") ?? "file";
                var line = JsonInt(item, "line");
                var context = JsonString(item, "context");
                var suffix = item.TryGetProperty("isDeclaration", out var isDeclaration) && isDeclaration.ValueKind == JsonValueKind.True
                    ? " · declaration"
                    : string.Empty;

                lines.Add($"{file}:{line}{suffix}");
                if (!string.IsNullOrWhiteSpace(context))
                {
                    lines.Add($"  {context}");
                }
            }

            var definitionCount = root.TryGetProperty("definitions", out var definitions) && definitions.ValueKind == JsonValueKind.Array
                ? definitions.GetArrayLength()
                : 0;
            var footer = $"{totalReferences} reference{(totalReferences == 1 ? string.Empty : "s")} · {definitionCount} definition{(definitionCount == 1 ? string.Empty : "s")}";
            if (root.TryGetProperty("truncated", out var truncated) && truncated.ValueKind == JsonValueKind.True)
            {
                footer += " · truncated";
            }

            return ConsoleUi.MessageBlock("refs", lines, footer);
        }
        catch
        {
            return ConsoleUi.ErrorBlock(output);
        }
    }

    private static string? JsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? JsonInt(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    // ── /compact ────────────────────────────────────────────────────────────────

    private async Task CompactSessionAsync()
    {
        var messages = _runtime.Session.Messages.ToList();
        if (messages.Count < CompactKeepTailCount + 2)
        {
            Console.WriteLine(ConsoleUi.MessageBlock("compact", "Session is too short to compact."));
            return;
        }

        Console.WriteLine(ConsoleUi.MessageBlock("compact", $"Summarizing {messages.Count} messages…"));

        var transcript = BuildTranscriptText(messages);
        var summaryPrompt =
            "Summarize the following conversation transcript as a concise numbered list of " +
            "key decisions, facts, file paths, and context established so far. " +
            "Be brief and factual. Output only the summary list, nothing else.\n\n" +
            transcript;

        // Isolate this API call from the real session
        _runtime.Session.Clear();
        string summaryText;
        try
        {
            var result = await _runtime.RunTurnAsync(summaryPrompt);
            summaryText = ExtractText(result);
        }
        catch (Exception ex)
        {
            // Restore original session on failure
            _runtime.Session.Clear();
            foreach (var m in messages)
                _runtime.Session.AddMessage(m);
            Console.WriteLine(ConsoleUi.ErrorBlock($"Compaction failed — session unchanged.\n{ex.Message}"));
            return;
        }

        if (string.IsNullOrWhiteSpace(summaryText))
        {
            _runtime.Session.Clear();
            foreach (var m in messages)
                _runtime.Session.AddMessage(m);
            Console.WriteLine(ConsoleUi.ErrorBlock("Compaction failed — model returned no summary."));
            return;
        }

        // Rebuild: summary synthetic message + last N original messages
        _runtime.Session.Clear();
        _runtime.Session.AddMessage(ConversationMessage.UserText(
            $"[Earlier conversation compacted — summary]\n{summaryText}"));
        foreach (var m in messages.TakeLast(CompactKeepTailCount))
            _runtime.Session.AddMessage(m);

        Console.WriteLine(ConsoleUi.MessageBlock(
            "compact",
            $"Session compacted: {messages.Count} → {_runtime.Session.Messages.Count} messages.",
            "Summary prepended · last 6 messages preserved."
        ));
    }

    // ── /diff ───────────────────────────────────────────────────────────────────

    private async Task HandleDiffAsync()
    {
        var (status, _) = await RunGitAsync("status", "--short");
        var (stat, _) = await RunGitAsync("diff", "--stat");
        var (diff, exitCode) = await RunGitAsync("diff");

        if (exitCode != 0 && string.IsNullOrWhiteSpace(status))
        {
            Console.WriteLine(ConsoleUi.ErrorBlock("Not a git repository or git not available."));
            return;
        }

        if (string.IsNullOrWhiteSpace(status) && string.IsNullOrWhiteSpace(diff))
        {
            Console.WriteLine(ConsoleUi.MessageBlock("diff", "No uncommitted changes."));
            return;
        }

        var output = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(stat))
        {
            output.AppendLine(stat.Trim());
        }

        if (!string.IsNullOrWhiteSpace(diff))
        {
            if (output.Length > 0) output.AppendLine();
            var diffLines = diff.Split('\n');
            var truncated = diffLines.Length > DiffMaxLines;
            var rendered = new StringBuilder();
            foreach (var line in diffLines.Take(DiffMaxLines))
            {
                rendered.AppendLine(line.StartsWith('+') && !line.StartsWith("+++")
                    ? ConsoleUi.DiffAdded(line)
                    : line.StartsWith('-') && !line.StartsWith("---")
                        ? ConsoleUi.DiffRemoved(line)
                        : line.StartsWith("@@")
                            ? ConsoleUi.DiffHunk(line)
                            : line);
            }
            output.Append(rendered);
            if (truncated)
                output.AppendLine(ConsoleUi.Muted($"… {diffLines.Length - DiffMaxLines} more lines not shown"));
        }

        Console.WriteLine(ConsoleUi.MessageBlock("diff", output.ToString().TrimEnd()));
    }

    // ── /commit ─────────────────────────────────────────────────────────────────

    private async Task HandleCommitAsync()
    {
        var (staged, _) = await RunGitAsync("diff", "--staged");
        var (unstaged, _) = await RunGitAsync("diff");

        if (string.IsNullOrWhiteSpace(staged) && string.IsNullOrWhiteSpace(unstaged))
        {
            Console.WriteLine(ConsoleUi.MessageBlock("commit", "Nothing to commit — working tree clean."));
            return;
        }

        if (string.IsNullOrWhiteSpace(staged))
        {
            Console.Error.Write(
                $"\n{ConsoleUi.Warning("?")} No staged changes. Stage all? " +
                $"{ConsoleUi.Muted("(y/n)")} ");
            var stageKey = Console.ReadKey(intercept: true);
            Console.Error.WriteLine();
            if (stageKey.Key != ConsoleKey.Y)
            {
                Console.WriteLine(ConsoleUi.MessageBlock("commit", "Aborted. Stage changes with `git add` first."));
                return;
            }
            await RunGitAsync("add", "-A");
            (staged, _) = await RunGitAsync("diff", "--staged");
        }

        Console.WriteLine(ConsoleUi.Muted("  Generating commit message…"));

        var commitPrompt =
            "Based on the following staged git diff, write a concise conventional commit message. " +
            "Output ONLY the commit message (subject line, blank line, optional short body). " +
            "No preamble, no explanation, no markdown.\n\n" + staged;

        // Isolated API call — save and restore session
        var savedMessages = _runtime.Session.Messages.ToList();
        _runtime.Session.Clear();
        string commitMessage;
        try
        {
            var result = await _runtime.RunTurnAsync(commitPrompt);
            commitMessage = ExtractText(result).Trim();
        }
        catch (Exception ex)
        {
            _runtime.Session.Clear();
            foreach (var m in savedMessages)
                _runtime.Session.AddMessage(m);
            Console.WriteLine(ConsoleUi.ErrorBlock($"Failed to generate commit message.\n{ex.Message}"));
            return;
        }
        finally
        {
            // Always restore real session
            _runtime.Session.Clear();
            foreach (var m in savedMessages)
                _runtime.Session.AddMessage(m);
        }

        if (string.IsNullOrWhiteSpace(commitMessage))
        {
            Console.WriteLine(ConsoleUi.ErrorBlock("Model returned no commit message."));
            return;
        }

        Console.WriteLine(ConsoleUi.MessageBlock("commit preview", commitMessage));
        Console.Error.Write($"{ConsoleUi.Warning("?")} Commit with this message? {ConsoleUi.Muted("(y/n)")} ");
        var confirm = Console.ReadKey(intercept: true);
        Console.Error.WriteLine();

        if (confirm.Key != ConsoleKey.Y)
        {
            Console.WriteLine(ConsoleUi.MessageBlock("commit", "Aborted."));
            return;
        }

        var (commitOutput, exitCode) = await RunGitAsync("commit", "-m", commitMessage);
        if (exitCode != 0)
            Console.WriteLine(ConsoleUi.ErrorBlock($"git commit failed:\n{commitOutput.Trim()}"));
        else
            Console.WriteLine(ConsoleUi.MessageBlock("commit", commitOutput.Trim()));
    }

    // ── /branch ─────────────────────────────────────────────────────────────────

    private async Task HandleBranchAsync(string? args)
    {
        var trimmed = args?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            var (output, _) = await RunGitAsync("branch", "--list", "--sort=-committerdate");
            if (string.IsNullOrWhiteSpace(output))
            {
                Console.WriteLine(ConsoleUi.MessageBlock("branch", "No branches found."));
                return;
            }

            var lines = output.Trim().Split('\n').Select(l => l.TrimEnd()).ToList();
            Console.WriteLine(ConsoleUi.MessageBlock("branch", lines, "Use /branch <name> to create and switch."));
            return;
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts[0].Equals("checkout", StringComparison.OrdinalIgnoreCase) && parts.Length == 2)
        {
            var (output, exitCode) = await RunGitAsync("switch", parts[1]);
            Console.WriteLine(exitCode == 0
                ? ConsoleUi.MessageBlock("branch", $"Switched to branch '{parts[1]}'.")
                : ConsoleUi.ErrorBlock($"git switch failed:\n{output.Trim()}"));
            return;
        }

        var branchName = trimmed;
        Console.Error.Write($"{ConsoleUi.Warning("?")} Create and switch to new branch '{branchName}'? {ConsoleUi.Muted("(y/n)")} ");
        var confirm = Console.ReadKey(intercept: true);
        Console.Error.WriteLine();
        if (confirm.Key != ConsoleKey.Y)
        {
            Console.WriteLine(ConsoleUi.MessageBlock("branch", "Aborted."));
            return;
        }

        var (createOutput, createExit) = await RunGitAsync("switch", "-c", branchName);
        Console.WriteLine(createExit == 0
            ? ConsoleUi.MessageBlock("branch", $"Created and switched to branch '{branchName}'.")
            : ConsoleUi.ErrorBlock($"git switch -c failed:\n{createOutput.Trim()}"));
    }

    // ── /worktree ────────────────────────────────────────────────────────────────

    private async Task HandleWorktreeAsync(string? args)
    {
        var trimmed = args?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            var (output, _) = await RunGitAsync("worktree", "list");
            Console.WriteLine(ConsoleUi.MessageBlock("worktree", string.IsNullOrWhiteSpace(output) ? "No worktrees." : output.Trim(),
                "Use /worktree add <path> [branch] or /worktree remove <path>."));
            return;
        }

        var parts = trimmed.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            Console.WriteLine(ConsoleUi.MessageBlock("worktree", "Usage:\n/worktree\n/worktree add <path> [branch]\n/worktree remove <path>"));
            return;
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "add":
            {
                var wtArgs = parts.Length == 3
                    ? new[] { "worktree", "add", parts[1], "-b", parts[2] }
                    : new[] { "worktree", "add", parts[1] };
                var (output, exitCode) = await RunGitAsync(wtArgs);
                Console.WriteLine(exitCode == 0
                    ? ConsoleUi.MessageBlock("worktree", $"Worktree created at '{parts[1]}'.\n{output.Trim()}")
                    : ConsoleUi.ErrorBlock($"git worktree add failed:\n{output.Trim()}"));
                break;
            }
            case "remove":
            {
                var (output, exitCode) = await RunGitAsync("worktree", "remove", parts[1]);
                Console.WriteLine(exitCode == 0
                    ? ConsoleUi.MessageBlock("worktree", $"Worktree '{parts[1]}' removed.")
                    : ConsoleUi.ErrorBlock($"git worktree remove failed:\n{output.Trim()}"));
                break;
            }
            default:
                Console.WriteLine(ConsoleUi.MessageBlock("worktree", "Unknown subcommand. Use: add, remove, or no args to list."));
                break;
        }
    }

    // ── /pr ─────────────────────────────────────────────────────────────────────

    private async Task HandlePrAsync(string? args)
    {
        var ghCheck = await RunCommandAsync("gh", ["--version"]);
        if (ghCheck.ExitCode != 0)
        {
            Console.WriteLine(ConsoleUi.ErrorBlock("GitHub CLI (gh) is not available. Install it from https://cli.github.com/"));
            return;
        }

        var (log, _) = await RunGitAsync("log", "origin/HEAD..HEAD", "--oneline", "--no-decorate");
        var (diff, _) = await RunGitAsync("diff", "origin/HEAD..HEAD");

        if (string.IsNullOrWhiteSpace(log) && string.IsNullOrWhiteSpace(diff))
        {
            Console.WriteLine(ConsoleUi.MessageBlock("pr", "No commits ahead of origin/HEAD. Nothing to open a PR for."));
            return;
        }

        Console.WriteLine(ConsoleUi.Muted("  Generating PR title and body…"));

        var prPrompt =
            "Based on the following git commits and diff, write a GitHub pull request title and body. " +
            "Output ONLY valid JSON in this format: {\"title\": \"...\", \"body\": \"...\"}. " +
            "Title must be under 70 characters. Body should be markdown with ## Summary, ## Test plan sections. " +
            "No preamble, no explanation, no extra text.\n\n" +
            $"Commits:\n{log}\n\nDiff (first 3000 chars):\n{diff[..Math.Min(diff.Length, 3000)]}";

        var savedMessages = _runtime.Session.Messages.ToList();
        _runtime.Session.Clear();
        string prTitle, prBody;
        try
        {
            var result = await _runtime.RunTurnAsync(prPrompt);
            var rawJson = ExtractText(result).Trim();
            using var doc = JsonDocument.Parse(rawJson);
            prTitle = doc.RootElement.GetProperty("title").GetString() ?? "chore: update";
            prBody = doc.RootElement.GetProperty("body").GetString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ConsoleUi.ErrorBlock($"Failed to generate PR content.\n{ex.Message}"));
            return;
        }
        finally
        {
            _runtime.Session.Clear();
            foreach (var m in savedMessages) _runtime.Session.AddMessage(m);
        }

        Console.WriteLine(ConsoleUi.MessageBlock("pr preview", $"**{prTitle}**\n\n{prBody}"));
        Console.Error.Write($"{ConsoleUi.Warning("?")} Open PR with this title and body? {ConsoleUi.Muted("(y/n)")} ");
        var confirm = Console.ReadKey(intercept: true);
        Console.Error.WriteLine();
        if (confirm.Key != ConsoleKey.Y)
        {
            Console.WriteLine(ConsoleUi.MessageBlock("pr", "Aborted."));
            return;
        }

        var ghResult = await RunCommandAsync("gh", ["pr", "create", "--title", prTitle, "--body", prBody]);
        if (ghResult.ExitCode != 0)
            Console.WriteLine(ConsoleUi.ErrorBlock($"gh pr create failed:\n{ghResult.Output.Trim()}"));
        else
            Console.WriteLine(ConsoleUi.MessageBlock("pr", ghResult.Output.Trim()));
    }

    // ── /memory ─────────────────────────────────────────────────────────────────

    private void HandleMemoryCommand(string? args)
    {
        var cwd = Directory.GetCurrentDirectory();
        var memoryDir = Path.Combine(cwd, ".codesharp", "memory");
        var memoryIndex = Path.Combine(cwd, ".codesharp", "MEMORY.md");

        var trimmed = args?.Trim();

        if (!string.IsNullOrWhiteSpace(trimmed))
        {
            var target = Path.Combine(memoryDir, trimmed.EndsWith(".md") ? trimmed : trimmed + ".md");
            if (!File.Exists(target))
            {
                Console.WriteLine(ConsoleUi.ErrorBlock($"Memory file not found: {trimmed}"));
                return;
            }

            var content = File.ReadAllText(target).Trim();
            Console.WriteLine(ConsoleUi.MessageBlock($"memory · {Path.GetFileNameWithoutExtension(target)}", content));
            return;
        }

        var lines = new List<string>();

        if (File.Exists(memoryIndex))
        {
            lines.Add("Index: .codesharp/MEMORY.md");
            lines.Add(string.Empty);
        }

        if (Directory.Exists(memoryDir))
        {
            var files = Directory.EnumerateFiles(memoryDir, "*.md").OrderBy(f => f).ToList();
            if (files.Count > 0)
            {
                lines.Add($"{files.Count} memory file{(files.Count == 1 ? string.Empty : "s")}:");
                foreach (var f in files)
                {
                    lines.Add($"  {Path.GetFileNameWithoutExtension(f)}");
                }
            }
            else
            {
                lines.Add("No memory files in .codesharp/memory/");
            }
        }
        else
        {
            lines.Add("No .codesharp/memory/ directory found.");
        }

        Console.WriteLine(ConsoleUi.MessageBlock("memory", lines,
            "Use /memory <filename> to view a specific memory. Edit .codesharp/memory/*.md to add memories."));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static string ExtractText(TurnSummary summary) =>
        string.Join("\n", summary.AssistantMessages
            .SelectMany(m => m.Blocks)
            .OfType<ContentBlock.Text>()
            .Select(b => b.Content.Trim())
            .Where(c => !string.IsNullOrWhiteSpace(c)));

    private static string BuildTranscriptText(IReadOnlyList<ConversationMessage> messages)
    {
        var sb = new StringBuilder();
        foreach (var msg in messages)
        {
            var role = msg.Role == MessageRole.User ? "User" : "Assistant";
            foreach (var block in msg.Blocks.OfType<ContentBlock.Text>())
            {
                if (!string.IsNullOrWhiteSpace(block.Content))
                    sb.AppendLine($"{role}: {block.Content.Trim()}");
            }
        }
        return sb.ToString();
    }

    private static Task<(string Output, int ExitCode)> RunGitAsync(params string[] arguments) =>
        RunCommandAsync("git", arguments);

    private static async Task<(string Output, int ExitCode)> RunCommandAsync(string fileName, IEnumerable<string> arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };
            foreach (var arg in arguments)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            return (output, process.ExitCode);
        }
        catch (Exception ex)
        {
            return (ex.Message, -1);
        }
    }

    private async Task ExportSessionAsync(string? path)
    {
        var exportPath = path ?? $"codesharp-session-{DateTime.UtcNow:yyyyMMdd-HHmmss}.md";

        var lines = new List<string>
        {
            "# CodeSharp Session Export",
            "",
            $"Model: {_model}",
            $"Exported: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC",
            $"Messages: {_runtime.Session.Messages.Count}",
            "",
            "---",
            ""
        };

        foreach (var msg in _runtime.Session.Messages)
        {
            lines.Add($"## {msg.Role}");
            lines.Add("");

            foreach (var block in msg.Blocks)
            {
                switch (block)
                {
                    case ContentBlock.Text t:
                        lines.Add(t.Content);
                        break;
                    case ContentBlock.ToolUse tu:
                        lines.Add($"[Tool: {tu.Name}]");
                        lines.Add(tu.Input);
                        break;
                    case ContentBlock.ToolResult tr:
                        lines.Add($"[Result: {tr.ToolName}]");
                        lines.Add(tr.Output);
                        break;
                }
                lines.Add("");
            }
        }

        await File.WriteAllLinesAsync(exportPath, lines);
        Console.WriteLine(ConsoleUi.MessageBlock(
            "export",
            $"Session exported to:\n{exportPath}"
        ));
    }

    private static string? GetGitBranch()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("branch");
            psi.ArgumentList.Add("--show-current");

            using var process = Process.Start(psi);
            if (process is null) return null;
            process.WaitForExit();
            return process.StandardOutput.ReadToEnd().Trim();
        }
        catch
        {
            return null;
        }
    }
}
