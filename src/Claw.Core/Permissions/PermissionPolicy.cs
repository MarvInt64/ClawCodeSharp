namespace Claw.Core;

public record PermissionRequest(
    string ToolName,
    string Input,
    string? Description = null
);

public enum PermissionDecision
{
    Allow,
    Deny,
}

public record PermissionPromptDecision(PermissionDecision Decision, string? Reason = null)
{
    public static PermissionPromptDecision Allow => new(PermissionDecision.Allow);
    public static PermissionPromptDecision Deny(string reason) => new(PermissionDecision.Deny, reason);
}

public interface IPermissionPrompter
{
    PermissionPromptDecision Decide(PermissionRequest request);
}

public enum PermissionOutcome
{
    Allow,
    Deny,
}

public record PermissionResult(PermissionOutcome Outcome, string? Reason = null)
{
    public static PermissionResult Allowed => new(PermissionOutcome.Allow);
    public static PermissionResult Denied(string reason) => new(PermissionOutcome.Deny, reason);
}

public class PermissionPolicy
{
    private readonly PermissionMode _mode;
    private readonly Dictionary<string, PermissionMode> _toolPermissions;
    
    public PermissionPolicy(PermissionMode mode, IReadOnlyDictionary<string, PermissionMode>? toolPermissions = null)
    {
        _mode = mode;
        _toolPermissions = toolPermissions is not null
            ? new Dictionary<string, PermissionMode>(toolPermissions)
            : new Dictionary<string, PermissionMode>();
    }
    
    public PermissionResult Authorize(string toolName, string input, IPermissionPrompter? prompter = null)
    {
        var requiredMode = GetRequiredPermissionMode(toolName);
        
        if (CanExecute(_mode, requiredMode))
        {
            if (prompter is not null)
            {
                var decision = prompter.Decide(new PermissionRequest(toolName, input));
                return decision.Decision == PermissionDecision.Allow
                    ? PermissionResult.Allowed
                    : PermissionResult.Denied(decision.Reason ?? "Permission denied by user");
            }
            return PermissionResult.Allowed;
        }
        
        if (prompter is not null)
        {
            var decision = prompter.Decide(new PermissionRequest(toolName, input,
                $"Tool '{toolName}' requires {requiredMode.AsString()} permission"));
            return decision.Decision == PermissionDecision.Allow
                ? PermissionResult.Allowed
                : PermissionResult.Denied(decision.Reason ?? "Permission denied by user");
        }
        
        return PermissionResult.Denied($"Tool '{toolName}' requires {requiredMode.AsString()} permission");
    }
    
    private PermissionMode GetRequiredPermissionMode(string toolName)
    {
        return _toolPermissions.TryGetValue(toolName, out var mode)
            ? mode
            : PermissionMode.ReadOnly;
    }
    
    private static bool CanExecute(PermissionMode current, PermissionMode required) =>
        required switch
        {
            PermissionMode.ReadOnly => true,
            PermissionMode.WorkspaceWrite => current is PermissionMode.WorkspaceWrite or PermissionMode.DangerFullAccess,
            PermissionMode.DangerFullAccess => current == PermissionMode.DangerFullAccess,
            _ => false
        };
}
