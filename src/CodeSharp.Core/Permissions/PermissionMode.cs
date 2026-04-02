namespace CodeSharp.Core;

public enum PermissionMode
{
    ReadOnly,
    WorkspaceWrite,
    DangerFullAccess,
}

public static class PermissionModeExtensions
{
    public static string AsString(this PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "read-only",
        PermissionMode.WorkspaceWrite => "workspace-write",
        PermissionMode.DangerFullAccess => "danger-full-access",
        _ => throw new InvalidOperationException($"Unknown permission mode: {mode}")
    };
    
    public static PermissionMode FromString(string value) => value.ToLowerInvariant() switch
    {
        "read-only" => PermissionMode.ReadOnly,
        "workspace-write" => PermissionMode.WorkspaceWrite,
        "danger-full-access" => PermissionMode.DangerFullAccess,
        _ => throw new ArgumentException($"Unknown permission mode: {value}")
    };
}
