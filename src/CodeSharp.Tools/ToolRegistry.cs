using CodeSharp.Core;

namespace CodeSharp.Tools;

public record ToolSpec(
    string Name,
    string Description,
    object InputSchema,
    PermissionMode RequiredPermission
);

public record ToolDefinition(
    string Name,
    string? Description = null,
    object? InputSchema = null
);

public class GlobalToolRegistry
{
    private readonly List<ToolSpec> _builtInTools;
    private readonly List<Plugins.PluginTool> _pluginTools;
    
    public GlobalToolRegistry() : this(Array.Empty<Plugins.PluginTool>()) { }
    
    public GlobalToolRegistry(IReadOnlyList<Plugins.PluginTool> pluginTools)
    {
        _builtInTools = GetBuiltInToolSpecs();
        
        var builtInNames = _builtInTools.Select(t => t.Name).ToHashSet();
        var seenPluginNames = new HashSet<string>();
        
        foreach (var tool in pluginTools)
        {
            var name = tool.Definition.Name;
            if (builtInNames.Contains(name))
                throw new InvalidOperationException($"Plugin tool '{name}' conflicts with a built-in tool name");
            if (!seenPluginNames.Add(name))
                throw new InvalidOperationException($"Duplicate plugin tool name '{name}'");
        }
        
        _pluginTools = pluginTools.ToList();
    }
    
    public IReadOnlyList<ToolSpec> BuiltInTools => _builtInTools;
    
    public IReadOnlyList<Plugins.PluginTool> PluginTools => _pluginTools;
    
    public IReadOnlyList<ToolSpec> GetAllTools()
    {
        var allTools = new List<ToolSpec>(_builtInTools);
        foreach (var pluginTool in _pluginTools)
        {
            var spec = new ToolSpec(
                pluginTool.Definition.Name,
                pluginTool.Definition.Description ?? string.Empty,
                pluginTool.Definition.InputSchema ?? new { },
                PermissionModeExtensions.FromString(pluginTool.RequiredPermission)
            );
            allTools.Add(spec);
        }
        return allTools;
    }
    
    public IReadOnlyList<ToolDefinition> GetDefinitions(ISet<string>? allowedTools = null)
    {
        var definitions = new List<ToolDefinition>();

        foreach (var spec in _builtInTools)
        {
            if (allowedTools is null || allowedTools.Contains(spec.Name))
            {
                definitions.Add(new ToolDefinition(spec.Name, spec.Description, spec.InputSchema));
            }
        }

        foreach (var tool in _pluginTools)
        {
            if (allowedTools is null || allowedTools.Contains(tool.Definition.Name))
            {
                definitions.Add(new ToolDefinition(
                    tool.Definition.Name,
                    tool.Definition.Description,
                    tool.Definition.InputSchema
                ));
            }
        }

        return definitions;
    }
    
    public IReadOnlyList<(string Name, PermissionMode Mode)> GetPermissionSpecs(ISet<string>? allowedTools = null)
    {
        var specs = new List<(string, PermissionMode)>();
        
        foreach (var spec in _builtInTools)
        {
            if (allowedTools is null || allowedTools.Contains(spec.Name))
            {
                specs.Add((spec.Name, spec.RequiredPermission));
            }
        }
        
        foreach (var tool in _pluginTools)
        {
            if (allowedTools is null || allowedTools.Contains(tool.Definition.Name))
            {
                specs.Add((tool.Definition.Name, PermissionModeExtensions.FromString(tool.RequiredPermission)));
            }
        }
        
        return specs;
    }
    
    public ISet<string>? NormalizeAllowedTools(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
            return null;
        
        var canonicalNames = GetAllTools().Select(t => t.Name).ToList();
        var nameMap = canonicalNames.ToDictionary(n => NormalizeToolName(n), n => n);
        
        nameMap["read"] = "read_file";
        nameMap["write"] = "write_file";
        nameMap["edit"] = "edit_file";
        nameMap["glob"] = "glob_search";
        nameMap["grep"] = "grep_search";
        
        var allowed = new HashSet<string>();
        
        foreach (var value in values)
        {
            foreach (var token in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var normalized = NormalizeToolName(token);
                if (nameMap.TryGetValue(normalized, out var canonical))
                {
                    allowed.Add(canonical);
                }
                else
                {
                    throw new ArgumentException($"Unsupported tool in --allowedTools: {token}");
                }
            }
        }
        
        return allowed;
    }
    
    private static string NormalizeToolName(string name) =>
        name.Trim().Replace('-', '_').ToLowerInvariant();
    
    private static List<ToolSpec> GetBuiltInToolSpecs() =>
    [
        new("bash", "Execute a shell command in the current workspace.",
            new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string" },
                    timeout = new { type = "integer", minimum = 1 },
                    description = new { type = "string" },
                    run_in_background = new { type = "boolean" },
                    dangerouslyDisableSandbox = new { type = "boolean" }
                },
                required = new[] { "command" },
                additionalProperties = false
            },
            PermissionMode.DangerFullAccess
        ),
        new("read_file", "Read a known text file from the workspace. Prefer glob_search or grep_search first when the target file is not already known.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string" },
                    offset = new { type = "integer", minimum = 0 },
                    limit = new { type = "integer", minimum = 1 }
                },
                required = new[] { "path" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("write_file", "Write a text file in the workspace.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string" },
                    content = new { type = "string" }
                },
                required = new[] { "path", "content" },
                additionalProperties = false
            },
            PermissionMode.WorkspaceWrite
        ),
        new("edit_file", "Replace text in a workspace file.",
            new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string" },
                    old_string = new { type = "string" },
                    new_string = new { type = "string" },
                    replace_all = new { type = "boolean" }
                },
                required = new[] { "path", "old_string", "new_string" },
                additionalProperties = false
            },
            PermissionMode.WorkspaceWrite
        ),
        new("glob_search", "Find candidate files by glob pattern. Use this to narrow down file paths before calling read_file. The result includes a total count plus matching paths.",
            new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string" },
                    path = new { type = "string" },
                    limit = new { type = "integer", minimum = 1 }
                },
                required = new[] { "pattern" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("grep_search", "Search file contents with a regex pattern. Use this before read_file when you need to find symbols, TODOs, placeholders, or unimplemented features. The result includes total match counts and a capped sample of matching lines.",
            new
            {
                type = "object",
                properties = new
                {
                    pattern = new { type = "string" },
                    path = new { type = "string" },
                    glob = new { type = "string" },
                    limit = new { type = "integer", minimum = 1 },
                    output_mode = new { type = "string" },
                    @n = new { type = "boolean" },
                    @i = new { type = "boolean" }
                },
                required = new[] { "pattern" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("WebFetch", "Fetch a URL, convert it into readable text, and answer a prompt about it.",
            new
            {
                type = "object",
                properties = new
                {
                    url = new { type = "string", format = "uri" },
                    prompt = new { type = "string" }
                },
                required = new[] { "url", "prompt" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("WebSearch", "Search the web for current information and return cited results.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", minLength = 2 },
                    allowed_domains = new { type = "array", items = new { type = "string" } },
                    blocked_domains = new { type = "array", items = new { type = "string" } }
                },
                required = new[] { "query" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("TodoWrite", "Update the structured task list for the current session.",
            new
            {
                type = "object",
                properties = new
                {
                    todos = new
                    {
                        type = "array",
                        items = new
                        {
                            type = "object",
                            properties = new
                            {
                                content = new { type = "string" },
                                activeForm = new { type = "string" },
                                status = new { type = "string", @enum = new[] { "pending", "in_progress", "completed" } }
                            },
                            required = new[] { "content", "activeForm", "status" },
                            additionalProperties = false
                        }
                    }
                },
                required = new[] { "todos" },
                additionalProperties = false
            },
            PermissionMode.WorkspaceWrite
        ),
        new("Skill", "Load a local skill definition and its instructions.",
            new
            {
                type = "object",
                properties = new
                {
                    skill = new { type = "string" },
                    args = new { type = "string" }
                },
                required = new[] { "skill" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("Agent", "Launch a specialized agent task and persist its handoff metadata.",
            new
            {
                type = "object",
                properties = new
                {
                    description = new { type = "string" },
                    prompt = new { type = "string" },
                    subagent_type = new { type = "string" },
                    name = new { type = "string" },
                    model = new { type = "string" }
                },
                required = new[] { "description", "prompt" },
                additionalProperties = false
            },
            PermissionMode.DangerFullAccess
        ),
        new("ToolSearch", "Search for deferred or specialized tools by exact name or keywords.",
            new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string" },
                    max_results = new { type = "integer", minimum = 1 }
                },
                required = new[] { "query" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("NotebookEdit", "Replace, insert, or delete a cell in a Jupyter notebook.",
            new
            {
                type = "object",
                properties = new
                {
                    notebook_path = new { type = "string" },
                    cell_id = new { type = "string" },
                    new_source = new { type = "string" },
                    cell_type = new { type = "string", @enum = new[] { "code", "markdown" } },
                    edit_mode = new { type = "string", @enum = new[] { "replace", "insert", "delete" } }
                },
                required = new[] { "notebook_path" },
                additionalProperties = false
            },
            PermissionMode.WorkspaceWrite
        ),
        new("Sleep", "Wait for a specified duration without holding a shell process.",
            new
            {
                type = "object",
                properties = new
                {
                    duration_ms = new { type = "integer", minimum = 0 }
                },
                required = new[] { "duration_ms" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("SendUserMessage", "Send a message to the user.",
            new
            {
                type = "object",
                properties = new
                {
                    message = new { type = "string" },
                    attachments = new { type = "array", items = new { type = "string" } },
                    status = new { type = "string", @enum = new[] { "normal", "proactive" } }
                },
                required = new[] { "message", "status" },
                additionalProperties = false
            },
            PermissionMode.ReadOnly
        ),
        new("Config", "Get or set CodeSharp settings.",
            new
            {
                type = "object",
                properties = new
                {
                    setting = new { type = "string" },
                    value = new { type = new[] { "string", "boolean", "number" } }
                },
                required = new[] { "setting" },
                additionalProperties = false
            },
            PermissionMode.WorkspaceWrite
        ),
        new("StructuredOutput", "Return structured output in the requested format.",
            new
            {
                type = "object",
                additionalProperties = true
            },
            PermissionMode.ReadOnly
        ),
        new("REPL", "Execute code in a REPL-like subprocess.",
            new
            {
                type = "object",
                properties = new
                {
                    code = new { type = "string" },
                    language = new { type = "string" },
                    timeout_ms = new { type = "integer", minimum = 1 }
                },
                required = new[] { "code", "language" },
                additionalProperties = false
            },
            PermissionMode.DangerFullAccess
        ),
        new("PowerShell", "Execute a PowerShell command with optional timeout.",
            new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string" },
                    timeout = new { type = "integer", minimum = 1 },
                    description = new { type = "string" },
                    run_in_background = new { type = "boolean" }
                },
                required = new[] { "command" },
                additionalProperties = false
            },
            PermissionMode.DangerFullAccess
        ),
    ];
}
