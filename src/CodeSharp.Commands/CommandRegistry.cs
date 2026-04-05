namespace CodeSharp.Commands;

public record SlashCommandSpec(
    string Name,
    IReadOnlyList<string> Aliases,
    string Description,
    bool IsResumeSafe = false,
    bool ShowInRepl = true
);

public class CommandRegistry
{
    private readonly List<SlashCommandSpec> _commands;
    
    public CommandRegistry()
    {
        _commands = GetBuiltInCommands();
    }
    
    public IReadOnlyList<SlashCommandSpec> Commands => _commands;

    public IReadOnlyList<SlashCommandSpec> VisibleCommands =>
        _commands.Where(static c => c.ShowInRepl).ToList();
    
    public SlashCommandSpec? GetCommand(string name)
    {
        return _commands.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }
    
    public IEnumerable<string> GetCompletionCandidates()
    {
        foreach (var cmd in VisibleCommands)
        {
            yield return $"/{cmd.Name}";
            foreach (var alias in cmd.Aliases)
            {
                yield return $"/{alias}";
            }
        }
    }
    
    private static List<SlashCommandSpec> GetBuiltInCommands() =>
    [
        new("help", ["h", "?"], "Show available commands", true),
        new("status", [], "Show session and workspace status", true),
        new("compact", [], "Trim session history", true),
        new("model", [], "Show or switch model"),
        new("permissions", [], "Show or switch permission mode"),
        new("cost", [], "Show token usage", true),
        new("clear", [], "Clear session history"),
        new("resume", [], "Resume a previous session", ShowInRepl: false),
        new("export", [], "Export session transcript", true),
        new("session", [], "Manage sessions", ShowInRepl: false),
        new("config", [], "View or edit global provider/model/API key defaults", true),
        new("memory", [], "View memory file contents", true, ShowInRepl: false),
        new("init", [], "Initialize .codesharp configuration", true, ShowInRepl: false),
        new("diff", [], "Show uncommitted changes", true),
        new("symbols", ["symbol", "sym"], "Find symbol declarations in the workspace", true),
        new("refs", ["references"], "Find symbol references in the workspace", true),
        new("version", ["v"], "Show version", true),
        new("plan", [], "Switch planning mode on/off or approve a plan", true),
        new("agents", [], "List available agents", true, ShowInRepl: false),
        new("skills", [], "List available skills", true, ShowInRepl: false),
        new("plugins", [], "Manage plugins", ShowInRepl: false),
        new("bughunter", [], "Run bug hunting agent", ShowInRepl: false),
        new("branch", [], "Create and switch to new branch", ShowInRepl: false),
        new("worktree", [], "Create a new git worktree", ShowInRepl: false),
        new("commit", [], "Create a git commit"),
        new("pr", [], "Create a pull request", ShowInRepl: false),
        new("issue", [], "Create a GitHub issue", ShowInRepl: false),
        new("commit-push-pr", ["cpp"], "Commit, push, and create PR", ShowInRepl: false),
        new("ultraplan", [], "Generate detailed implementation plan", ShowInRepl: false),
        new("teleport", [], "Jump to a different directory", ShowInRepl: false),
        new("debug-tool-call", [], "Debug tool execution", ShowInRepl: false)
    ];
}
