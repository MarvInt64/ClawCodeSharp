namespace CodeSharp.Commands;

public record SlashCommandSpec(
    string Name,
    IReadOnlyList<string> Aliases,
    string Description,
    bool IsResumeSafe = false
);

public class CommandRegistry
{
    private readonly List<SlashCommandSpec> _commands;
    
    public CommandRegistry()
    {
        _commands = GetBuiltInCommands();
    }
    
    public IReadOnlyList<SlashCommandSpec> Commands => _commands;
    
    public SlashCommandSpec? GetCommand(string name)
    {
        return _commands.FirstOrDefault(c =>
            c.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            c.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }
    
    public IEnumerable<string> GetCompletionCandidates()
    {
        foreach (var cmd in _commands)
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
        new("resume", [], "Resume a previous session"),
        new("export", [], "Export session transcript", true),
        new("session", [], "Manage sessions"),
        new("config", [], "View or edit global provider/model/API key defaults", true),
        new("memory", [], "View memory file contents", true),
        new("init", [], "Initialize .codesharp configuration", true),
        new("diff", [], "Show uncommitted changes", true),
        new("version", ["v"], "Show version", true),
        new("agents", [], "List available agents", true),
        new("skills", [], "List available skills", true),
        new("plugins", [], "Manage plugins"),
        new("bughunter", [], "Run bug hunting agent"),
        new("branch", [], "Create and switch to new branch"),
        new("worktree", [], "Create a new git worktree"),
        new("commit", [], "Create a git commit"),
        new("pr", [], "Create a pull request"),
        new("issue", [], "Create a GitHub issue"),
        new("commit-push-pr", ["cpp"], "Commit, push, and create PR"),
        new("ultraplan", [], "Generate detailed implementation plan"),
        new("teleport", [], "Jump to a different directory"),
        new("debug-tool-call", [], "Debug tool execution")
    ];
}
