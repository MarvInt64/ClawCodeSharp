using CodeSharp.Api;
using CodeSharp.Commands;
using CodeSharp.Core;
using CodeSharp.Lsp;
using CodeSharp.Plugins;
using CodeSharp.Tools;

namespace CodeSharp.Cli;

public enum CliAction
{
    Repl,
    Prompt,
    Login,
    Logout,
    Init,
    Help,
    Version,
    Resume,
    SystemPrompt,
    DumpManifests,
    Agents,
    Skills,
}

public record CliOptions(
    CliAction Action,
    string? Prompt = null,
    string Model = ModelAliases.DefaultModel,
    PermissionMode PermissionMode = PermissionMode.DangerFullAccess,
    ProviderKind? Provider = null,
    string? SessionPath = null,
    string? OutputFormat = null,
    IReadOnlyList<string>? AllowedTools = null,
    IReadOnlyList<string>? ResumeCommands = null,
    string? Args = null,
    string? Cwd = null,
    string? Date = null
);

public class ArgumentParser
{
    public CliOptions Parse(string[] args)
    {
        var model = ModelAliases.DefaultModel;
        var permissionMode = PermissionMode.DangerFullAccess;
        ProviderKind? provider = null;
        var allowedTools = new List<string>();
        var outputFormat = "text";
        string? prompt = null;
        var action = CliAction.Repl;
        string? sessionPath = null;
        var resumeCommands = new List<string>();
        string? argsValue = null;
        string? cwd = null;
        string? date = null;
        
        var i = 0;
        while (i < args.Length)
        {
            switch (args[i])
            {
                case "-p" when i + 1 < args.Length:
                    prompt = args[++i];
                    action = CliAction.Prompt;
                    break;
                    
                case "--model" when i + 1 < args.Length:
                    model = ModelAliases.ResolveModelAlias(args[++i]);
                    break;
                    
                case var m when m.StartsWith("--model="):
                    model = ModelAliases.ResolveModelAlias(m[8..]);
                    break;
                    
                case "--permission-mode" when i + 1 < args.Length:
                    permissionMode = PermissionModeExtensions.FromString(args[++i]);
                    break;
                    
                case var pm when pm.StartsWith("--permission-mode="):
                    permissionMode = PermissionModeExtensions.FromString(pm[18..]);
                    break;
                    
                case "--provider" when i + 1 < args.Length:
                    provider = ParseProvider(args[++i]);
                    break;
                    
                case var p when p.StartsWith("--provider="):
                    provider = ParseProvider(p[11..]);
                    break;
                    
                case "--output" when i + 1 < args.Length:
                    outputFormat = args[++i];
                    break;
                    
                case "--allowedTools" when i + 1 < args.Length:
                    allowedTools.Add(args[++i]);
                    break;
                    
                case "--dangerously-skip-permissions":
                    permissionMode = PermissionMode.DangerFullAccess;
                    break;
                    
                case "--version" or "-V":
                    action = CliAction.Version;
                    break;
                    
                case "--resume" when i + 1 < args.Length:
                    action = CliAction.Resume;
                    sessionPath = args[++i];
                    i++;
                    while (i < args.Length && args[i].StartsWith('/'))
                    {
                        resumeCommands.Add(args[i]);
                        i++;
                    }
                    break;
                    
                case "--help" or "-h":
                    action = CliAction.Help;
                    break;
                    
                case "login":
                    action = CliAction.Login;
                    break;
                    
                case "logout":
                    action = CliAction.Logout;
                    break;
                    
                case "init":
                    action = CliAction.Init;
                    break;
                    
                case "system-prompt":
                    action = CliAction.SystemPrompt;
                    i++;
                    while (i < args.Length)
                    {
                        switch (args[i])
                        {
                            case "--cwd" when i + 1 < args.Length:
                                cwd = args[++i];
                                break;
                            case "--date" when i + 1 < args.Length:
                                date = args[++i];
                                break;
                            default:
                                i++;
                                break;
                        }
                    }
                    break;
                    
                case "agents":
                    action = CliAction.Agents;
                    i++;
                    if (i < args.Length)
                    {
                        argsValue = string.Join(' ', args[i..]);
                    }
                    break;
                    
                case "skills":
                    action = CliAction.Skills;
                    i++;
                    if (i < args.Length)
                    {
                        argsValue = string.Join(' ', args[i..]);
                    }
                    break;
                    
                default:
                    if (args[i].StartsWith('/'))
                    {
                        throw new ArgumentException($"Direct slash command '{args[i]}' is not available outside REPL. Start 'codesharp' to use interactive slash commands.");
                    }
                    
                    if (prompt is null)
                    {
                        prompt = args[i];
                        action = CliAction.Prompt;
                    }
                    else
                    {
                        prompt += " " + args[i];
                    }
                    break;
            }
            
            i++;
        }
        
        return new CliOptions(
            action,
            prompt,
            model,
            permissionMode,
            provider,
            sessionPath,
            outputFormat,
            allowedTools.Count > 0 ? allowedTools : null,
            resumeCommands.Count > 0 ? resumeCommands : null,
            argsValue,
            cwd,
            date
        );
    }
    
    private static ProviderKind ParseProvider(string value) => value.ToLowerInvariant() switch
    {
        "anthropic" => ProviderKind.CodeSharpApi,
        "openai" => ProviderKind.OpenAi,
        "xai" => ProviderKind.Xai,
        "nvidia" => ProviderKind.Nvidia,
        _ => throw new ArgumentException($"Unknown provider: {value}")
    };
}
