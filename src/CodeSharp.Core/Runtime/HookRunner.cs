namespace CodeSharp.Core;

public record HookRunResult(IReadOnlyList<string> Messages, bool IsDenied)
{
    public static HookRunResult Allowed(params string[] messages) => new(messages, false);
    public static HookRunResult Denied(params string[] messages) => new(messages, true);
}

public class HookRunner
{
    private readonly IReadOnlyList<string> _preToolUseHooks;
    private readonly IReadOnlyList<string> _postToolUseHooks;
    
    public static HookRunner Default { get; } = new(Array.Empty<string>(), Array.Empty<string>());
    
    public HookRunner(IReadOnlyList<string> preToolUseHooks, IReadOnlyList<string> postToolUseHooks)
    {
        _preToolUseHooks = preToolUseHooks;
        _postToolUseHooks = postToolUseHooks;
    }
    
    public Task<HookRunResult> RunPreToolUseAsync(string toolName, string input, CancellationToken cancellationToken = default)
    {
        var messages = new List<string>();
        foreach (var hook in _preToolUseHooks)
        {
            var result = RunHookCommand(hook, toolName, input, null, false);
            messages.AddRange(result.Messages);
            if (result.IsDenied)
                return Task.FromResult(HookRunResult.Denied(messages.ToArray()));
        }
        return Task.FromResult(HookRunResult.Allowed(messages.ToArray()));
    }
    
    public Task<HookRunResult> RunPostToolUseAsync(
        string toolName,
        string input,
        string output,
        bool isError,
        CancellationToken cancellationToken = default
    )
    {
        var messages = new List<string>();
        foreach (var hook in _postToolUseHooks)
        {
            var result = RunHookCommand(hook, toolName, input, output, isError);
            messages.AddRange(result.Messages);
            if (result.IsDenied)
                return Task.FromResult(HookRunResult.Denied(messages.ToArray()));
        }
        return Task.FromResult(HookRunResult.Allowed(messages.ToArray()));
    }
    
    private static HookRunResult RunHookCommand(
        string command,
        string toolName,
        string input,
        string? output,
        bool isError
    )
    {
        return HookRunResult.Allowed();
    }
}
