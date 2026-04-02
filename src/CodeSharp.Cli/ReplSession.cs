using CodeSharp.Commands;
using CodeSharp.Core;

namespace CodeSharp.Cli;

public class ReplSession
{
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
                Console.WriteLine(ConsoleUi.MessageBlock("compact", "Session compaction is not implemented yet."));
                return false;
                
            case SlashCommandKind.Init:
                Console.WriteLine(ConsoleUi.MessageBlock("init", "Init from inside REPL is not implemented yet."));
                return false;
                
            case SlashCommandKind.Diff:
                Console.WriteLine(ConsoleUi.MessageBlock("diff", "Diff rendering is not implemented yet."));
                return false;
                
            case SlashCommandKind.Config:
            {
                var result = ConfigCommandProcessor.Process(command.Section, _globalSettingsStore);
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
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = "branch --show-current",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            
            using var process = System.Diagnostics.Process.Start(startInfo);
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
