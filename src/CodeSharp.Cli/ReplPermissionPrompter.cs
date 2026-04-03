using CodeSharp.Core;

namespace CodeSharp.Cli;

internal sealed class ReplPermissionPrompter : IPermissionPrompter
{
    // Signals to the main REPL loop that it should not read console keys
    private static volatile int _consoleLock = 0;

    // Tools that always require explicit confirmation regardless of permission mode
    private static readonly HashSet<string> DangerTools = new(StringComparer.Ordinal)
        { "bash", "PowerShell", "REPL", "Agent" };

    private readonly PermissionMode _mode;
    private readonly HashSet<string> _alwaysAllow = new(StringComparer.Ordinal);

    public ReplPermissionPrompter(PermissionMode mode) => _mode = mode;

    public static bool IsConsoleActive => _consoleLock == 1;

    public PermissionPromptDecision Decide(PermissionRequest request)
    {
        if (_alwaysAllow.Contains(request.ToolName))
            return PermissionPromptDecision.Allow;

        // workspace-write mode: file tools are already permitted — only prompt for bash/shell/agent
        if (_mode == PermissionMode.WorkspaceWrite && !DangerTools.Contains(request.ToolName))
            return PermissionPromptDecision.Allow;

        Interlocked.Exchange(ref _consoleLock, 1);
        try
        {
            Console.Error.Write(
                $"\n\u001b[33m?\u001b[0m Allow \u001b[1m{request.ToolName}\u001b[0m?" +
                " \u001b[2m(y = yes · n = no · a = always)\u001b[0m ");

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                switch (key.Key)
                {
                    case ConsoleKey.Y:
                        Console.Error.WriteLine("\u001b[32my\u001b[0m");
                        return PermissionPromptDecision.Allow;
                    case ConsoleKey.N:
                        Console.Error.WriteLine("\u001b[31mn\u001b[0m");
                        return PermissionPromptDecision.Deny("User denied");
                    case ConsoleKey.A:
                        Console.Error.WriteLine("\u001b[32ma (always)\u001b[0m");
                        _alwaysAllow.Add(request.ToolName);
                        return PermissionPromptDecision.Allow;
                    case ConsoleKey.Escape:
                        Console.Error.WriteLine("\u001b[31mesc\u001b[0m");
                        return PermissionPromptDecision.Deny(ConversationRuntime.UserInterruptMessage);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _consoleLock, 0);
        }
    }
}
