using System.Text.Json;

namespace CodeSharp.Plugins;

public class PluginManager
{
    private readonly string _workingDirectory;
    private readonly List<PluginDefinition> _plugins = new();
    private readonly List<PluginTool> _aggregatedTools = new();
    
    public PluginManager(string workingDirectory)
    {
        _workingDirectory = workingDirectory;
    }
    
    public IReadOnlyList<PluginDefinition> Plugins => _plugins;
    
    public IReadOnlyList<PluginTool> AggregatedTools => _aggregatedTools;
    
    public void LoadFromConfig(string configPath)
    {
        if (!File.Exists(configPath))
            return;
        
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<PluginConfigRoot>(json);
        
        if (config?.Plugins is null)
            return;
        
        foreach (var pluginConfig in config.Plugins)
        {
            LoadPlugin(pluginConfig);
        }
    }
    
    private void LoadPlugin(PluginConfig pluginConfig)
    {
        var manifestPath = pluginConfig.Path;
        if (!Path.IsPathRooted(manifestPath))
        {
            manifestPath = Path.GetFullPath(manifestPath, _workingDirectory);
        }
        
        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"Warning: Plugin manifest not found at {manifestPath}");
            return;
        }
        
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<PluginManifestDto>(json);
        
        if (manifest is null)
            return;
        
        var tools = new List<ToolDefinition>();
        foreach (var tool in manifest.Tools ?? new List<PluginToolDto>())
        {
            tools.Add(new ToolDefinition(
                tool.Name ?? string.Empty,
                tool.Description,
                tool.InputSchema
            ));
        }
        
        var hooks = manifest.Hooks is not null
            ? new PluginHooks(
                manifest.Hooks.PreToolUse,
                manifest.Hooks.PostToolUse
            )
            : null;
        
        var definition = new PluginDefinition(
            manifest.Name ?? Path.GetFileNameWithoutExtension(manifestPath),
            manifest.Version ?? "1.0.0",
            tools,
            hooks
        );
        
        _plugins.Add(definition);
        
        foreach (var tool in tools)
        {
            var pluginTool = new PluginTool(
                tool,
                "read-only",
                async input =>
                {
                    await Task.CompletedTask;
                    return JsonSerializer.Serialize(new { status = "Plugin tool execution not implemented" });
                }
            );
            _aggregatedTools.Add(pluginTool);
        }
    }
    
    public IReadOnlyList<PluginTool> GetAggregatedTools() => _aggregatedTools;
}

internal class PluginConfigRoot
{
    public List<PluginConfig>? Plugins { get; set; }
}

internal class PluginConfig
{
    public string Path { get; set; } = string.Empty;
}

internal class PluginManifestDto
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public List<PluginToolDto>? Tools { get; set; }
    public PluginHooksDto? Hooks { get; set; }
}

internal class PluginToolDto
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public object? InputSchema { get; set; }
}

internal class PluginHooksDto
{
    public List<string>? PreToolUse { get; set; }
    public List<string>? PostToolUse { get; set; }
}
