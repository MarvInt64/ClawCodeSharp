namespace CodeSharp.Commands;

public enum SlashCommandKind
{
    Help,
    Status,
    Compact,
    Model,
    Permissions,
    Cost,
    Clear,
    Resume,
    Export,
    Session,
    Config,
    Memory,
    Init,
    Diff,
    Symbols,
    References,
    Version,
    Agents,
    Skills,
    Bughunter,
    Branch,
    Worktree,
    Commit,
    Pr,
    Issue,
    CommitPushPr,
    Plan,
    Ultraplan,
    Teleport,
    DebugToolCall,
    Plugins,
    Unknown,
}

public record SlashCommand
{
    public SlashCommandKind Kind { get; init; }
    public string? Name { get; init; }
    public string? Args { get; init; }
    public bool? Confirm { get; init; }
    public string? Path { get; init; }
    public string? Section { get; init; }
    
    public static SlashCommand Help => new() { Kind = SlashCommandKind.Help };
    public static SlashCommand Status => new() { Kind = SlashCommandKind.Status };
    public static SlashCommand Compact => new() { Kind = SlashCommandKind.Compact };
    public static SlashCommand Version => new() { Kind = SlashCommandKind.Version };
    
    public static SlashCommand Model(string? args = null) => new()
    {
        Kind = SlashCommandKind.Model,
        Args = args
    };
    
    public static SlashCommand Permissions(string? args = null) => new()
    {
        Kind = SlashCommandKind.Permissions,
        Args = args
    };
    
    public static SlashCommand Cost => new() { Kind = SlashCommandKind.Cost };
    
    public static SlashCommand Clear(bool confirm = false) => new()
    {
        Kind = SlashCommandKind.Clear,
        Confirm = confirm
    };
    
    public static SlashCommand Export(string? path = null) => new()
    {
        Kind = SlashCommandKind.Export,
        Path = path
    };
    
    public static SlashCommand Session(string? args = null) => new()
    {
        Kind = SlashCommandKind.Session,
        Args = args
    };
    
    public static SlashCommand Config(string? section = null) => new()
    {
        Kind = SlashCommandKind.Config,
        Section = section
    };
    
    public static SlashCommand Memory => new() { Kind = SlashCommandKind.Memory };
    public static SlashCommand Init => new() { Kind = SlashCommandKind.Init };
    public static SlashCommand Diff => new() { Kind = SlashCommandKind.Diff };
    public static SlashCommand Symbols(string? args = null) => new()
    {
        Kind = SlashCommandKind.Symbols,
        Args = args
    };
    public static SlashCommand References(string? args = null) => new()
    {
        Kind = SlashCommandKind.References,
        Args = args
    };
    
    public static SlashCommand Agents(string? args = null) => new()
    {
        Kind = SlashCommandKind.Agents,
        Args = args
    };
    
    public static SlashCommand Skills(string? args = null) => new()
    {
        Kind = SlashCommandKind.Skills,
        Args = args
    };
    
    public static SlashCommand Plugins(string? args = null) => new()
    {
        Kind = SlashCommandKind.Plugins,
        Args = args
    };

    public static SlashCommand Plan(string? args = null) => new()
    {
        Kind = SlashCommandKind.Plan,
        Args = args
    };
    
    public static SlashCommand Unknown(string name) => new()
    {
        Kind = SlashCommandKind.Unknown,
        Name = name
    };
    
    public static SlashCommand? Parse(string input)
    {
        var trimmed = input.Trim();
        if (!trimmed.StartsWith('/'))
            return null;
        
        var parts = trimmed[1..].Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0].ToLowerInvariant();
        var args = parts.Length > 1 ? parts[1] : null;
        
        return command switch
        {
            "help" or "h" or "?" => Help,
            "status" => Status,
            "compact" => Compact,
            "model" => Model(args),
            "permissions" => Permissions(args),
            "cost" => Cost,
            "clear" => new SlashCommand
            {
                Kind = SlashCommandKind.Clear,
                Confirm = args == "--confirm"
            },
            "resume" => new SlashCommand
            {
                Kind = SlashCommandKind.Resume,
                Path = args
            },
            "export" => Export(args),
            "session" => Session(args),
            "config" => Config(args),
            "memory" => Memory,
            "init" => Init,
            "diff" => Diff,
            "symbols" or "symbol" or "sym" => Symbols(args),
            "refs" or "references" => References(args),
            "version" or "v" => Version,
            "agents" => Agents(args),
            "skills" => Skills(args),
            "plugins" => Plugins(args),
            "bughunter" => new SlashCommand { Kind = SlashCommandKind.Bughunter, Args = args },
            "branch" => new SlashCommand { Kind = SlashCommandKind.Branch, Args = args },
            "worktree" => new SlashCommand { Kind = SlashCommandKind.Worktree, Args = args },
            "commit" => new SlashCommand { Kind = SlashCommandKind.Commit },
            "pr" => new SlashCommand { Kind = SlashCommandKind.Pr, Args = args },
            "issue" => new SlashCommand { Kind = SlashCommandKind.Issue, Args = args },
            "commit-push-pr" or "cpp" => new SlashCommand { Kind = SlashCommandKind.CommitPushPr },
            "plan" => Plan(args),
            "ultraplan" => new SlashCommand { Kind = SlashCommandKind.Ultraplan, Args = args },
            "teleport" => new SlashCommand { Kind = SlashCommandKind.Teleport, Args = args },
            "debug-tool-call" => new SlashCommand { Kind = SlashCommandKind.DebugToolCall },
            _ => Unknown(command)
        };
    }
}
