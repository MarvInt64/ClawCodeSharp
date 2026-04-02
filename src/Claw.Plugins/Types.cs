namespace Claw.Plugins;

public record ToolDefinition(
    string Name,
    string? Description = null,
    object? InputSchema = null
);

public record PluginTool(
    ToolDefinition Definition,
    string RequiredPermission,
    Func<string, Task<string>> Execute
);

public record PluginManifest(
    string Name,
    string Version,
    IReadOnlyList<PluginTool> Tools,
    PluginHooks? Hooks = null
);

public record PluginHooks(
    IReadOnlyList<string>? PreToolUse = null,
    IReadOnlyList<string>? PostToolUse = null
);

public record PluginDefinition(
    string Name,
    string Version,
    IReadOnlyList<ToolDefinition> Tools,
    PluginHooks? Hooks = null
);
