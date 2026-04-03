using System.Diagnostics;
using System.Text;
using CodeSharp.Commands;
using CodeSharp.Core;

namespace CodeSharp.Cli;

public class ReplSession
{
    private const int CompactKeepTailCount = 6;
    private const int DiffMaxLines = 200;

    private readonly ConversationRuntime _runtime;
    private readonly string _model;
    private readonly string _provider;
    private readonly PermissionMode _permissionMode;
    private readonly CommandRegistry _commandRegistry;
    private readonly UsageTracker _usageTracker;
    private readonly string _sessionPath;
    private readonly GlobalSettingsStore _globalSettingsStore;

    public ReplSession(
        ConversationRuntime runtime,
        string model,
        string provider,
        PermissionMode permissionMode,
        string sessionPath
    )
    {
        _runtime = runtime;
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
                ("Permissions", _permissionMode.AsString()),
                ("Session", Path.GetFileNameWithoutExtension(_sessionPath))
            ],
            "/help · /config · /status · type while thinking to queue · Ctrl+C cancels · Ctrl+C twice asks to quit"
        );
    }

    public async Task<bool> HandleCommandAsync(SlashCommand command)
    {
        switch (command.Kind)
        {
            case SlashCommandKind.Help:
                Console.WriteLine(CommandHandlers.RenderHelp(_commandRegistry));
                return false;

            case SlashCommandKind.Status:
                Console.WriteLine(CommandHandlers.RenderStatus(
                    _model,
                    _permissionMode,
                    _runtime.Session.Messages.Count,
                    _usageTracker.Turns(),
                    _usageTracker.CumulativeUsage().TotalTokens,
                    GetGitBranch()
                ));
                return false;

            case SlashCommandKind.Cost:
                Console.WriteLine(CommandHandlers.RenderCost(_usageTracker.CumulativeUsage(), _usageTracker.Turns()));
                return false;

            case SlashCommandKind.Model:
                Console.WriteLine(CommandHandlers.RenderModelReport(_model, _runtime.Session.Messages.Count, _usageTracker.Turns()));
                return false;

            case SlashCommandKind.Permissions:
                Console.WriteLine(CommandHandlers.RenderPermissionsReport(_permissionMode.AsString()));
                return false;

            case SlashCommandKind.Version:
                Console.WriteLine(CommandHandlers.RenderVersion("0.1.0"));
                return false;

            case SlashCommandKind.Export:
                await ExportSessionAsync(command.Path);
                return true;

            case SlashCommandKind.Clear when command.Confirm == true:
                _runtime.Session.Clear();
                Console.WriteLine(ConsoleUi.MessageBlock("session", "Session history cleared."));
                return true;

            case SlashCommandKind.Clear:
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "clear",
                    "Confirmation required.",
                    "Run /clear --confirm to remove the current session history."
                ));
                return false;

            case SlashCommandKind.Compact:
                await CompactSessionAsync();
                return false;

            case SlashCommandKind.Diff:
                await HandleDiffAsync();
                return false;

            case SlashCommandKind.Commit:
                await HandleCommitAsync();
                return false;

            case SlashCommandKind.Init:
                Console.WriteLine(ConsoleUi.MessageBlock("init", "Init from inside REPL is not implemented yet."));
                return false;

            case SlashCommandKind.Config:
            {
                var headerLines = StartupBanner().Replace("\r\n", "\n").Split('\n');
                var result = ConfigCommandProcessor.Process(command.Section, _globalSettingsStore, headerLines);
                var footer = string.IsNullOrWhiteSpace(command.Section)
                    ? "Global defaults apply to new CodeSharp sessions."
                    : result.Footer;
                Console.WriteLine(ConsoleUi.MessageBlock(result.Title, result.Body, footer));
                return false;
            }

            case SlashCommandKind.Memory:
                Console.WriteLine(ConsoleUi.MessageBlock("memory", "Memory inspection is not implemented yet."));
                return false;

            case SlashCommandKind.Agents:
                Console.WriteLine(ConsoleUi.MessageBlock("agents", "Agent management in REPL is not implemented yet."));
                return false;

            case SlashCommandKind.Skills:
                Console.WriteLine(ConsoleUi.MessageBlock("skills", "Skill management in REPL is not implemented yet."));
                return false;

            case SlashCommandKind.Plugins:
                Console.WriteLine(ConsoleUi.MessageBlock("plugins", "Plugin management in REPL is not implemented yet."));
                return false;

            case SlashCommandKind.Unknown:
                Console.WriteLine(ConsoleUi.ErrorBlock(
                    $"Unknown command: /{command.Name}\nType /help for available commands."
                ));
                return false;

            default:
                Console.WriteLine(ConsoleUi.MessageBlock(
                    "todo",
                    $"Command '{command.Kind}' is not implemented yet."
                ));
                return false;
        }
    }

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

    private static async Task<(string Output, int ExitCode)> RunGitAsync(params string[] arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
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
