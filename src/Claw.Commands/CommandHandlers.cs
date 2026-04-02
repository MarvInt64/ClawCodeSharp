using Claw.Core;

namespace Claw.Commands;

public static class CommandHandlers
{
    public static string RenderHelp(CommandRegistry registry)
    {
        var lines = new List<string>
        {
            "Help",
            "  Slash commands   Type /help, /status, /cost, /model, /permissions, /export",
            "  Exit             /exit or /quit",
            "",
            "Commands"
        };
        
        foreach (var cmd in registry.Commands.OrderBy(c => c.Name))
        {
            var aliases = cmd.Aliases.Count > 0
                ? $" ({string.Join(", ", cmd.Aliases)})"
                : "";
            lines.Add($"  /{cmd.Name}{aliases,-20} {cmd.Description}");
        }
        
        lines.Add("");
        lines.Add("Tip");
        lines.Add("  Tab completes slash commands");
        lines.Add("  Type while the model is thinking to queue the next task");
        lines.Add("  Ctrl+C cancels the active turn");
        lines.Add("  Press Ctrl+C twice quickly to get a quit confirmation");
        
        return string.Join("\n", lines);
    }
    
    public static string RenderStatus(
        string model,
        PermissionMode mode,
        int messageCount,
        int turns,
        long totalTokens,
        string? gitBranch
    )
    {
        var lines = new List<string>
        {
            "Status",
            $"  Model            {model}",
            $"  Permissions      {mode.AsString()}",
            $"  Activity         {messageCount} messages · {turns} turns",
            $"  Tokens           {totalTokens:N0}"
        };
        
        if (gitBranch is not null)
        {
            lines.Add($"  Git branch       {gitBranch}");
        }

        lines.Add("");
        lines.Add("Next");
        lines.Add("  /cost            Inspect token usage");
        lines.Add("  /export          Save the transcript");
        
        return string.Join("\n", lines);
    }
    
    public static string RenderCost(Core.TokenUsage usage, int turns)
    {
        return $@"Cost
  Input tokens     {usage.InputTokens:N0}
  Output tokens    {usage.OutputTokens:N0}
  Cache create     {usage.CacheCreationInputTokens:N0}
  Cache read       {usage.CacheReadInputTokens:N0}
  Total tokens     {usage.TotalTokens:N0}
  Turns            {turns}

Next
  /status          Review session context";
    }
    
    public static string RenderModelReport(string model, int messageCount, int turns)
    {
        return $@"Model
  Current          {model}
  Session          {messageCount} messages · {turns} turns

Aliases
  opus             claude-opus-4-6
  sonnet           claude-sonnet-4-6
  haiku            claude-haiku-4-5-20251213

Next
  /model            Show the current model
  /model <name>     Switch models for this REPL session";
    }
    
    public static string RenderPermissionsReport(string mode)
    {
        var modes = new[]
        {
            ("read-only", "Read/search tools only", mode == "read-only"),
            ("workspace-write", "Edit files inside the workspace", mode == "workspace-write"),
            ("danger-full-access", "Unrestricted tool access", mode == "danger-full-access")
        };
        
        var modeLines = modes.Select(m =>
        {
            var marker = m.Item3 ? "● current" : "○ available";
            return $"  {m.Item1,-18} {marker,-11} {m.Item2}";
        });
        
        var effect = mode switch
        {
            "read-only" => "Only read/search tools can run automatically",
            "workspace-write" => "Editing tools can modify files in the workspace",
            "danger-full-access" => "All tools can run without additional sandbox limits",
            _ => "Unknown permission mode"
        };
        
        return $@"Permissions
  Active mode      {mode}
  Effect           {effect}

Modes
{string.Join("\n", modeLines)}

Next
  /permissions        Show the current mode
  /permissions <mode> Switch modes for subsequent tool calls";
    }
    
    public static string RenderVersion(string version)
    {
        return $@"Version
  ClawCodeSharp       {version}";
    }
}
