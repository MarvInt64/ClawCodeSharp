using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeSharp.Core;

namespace CodeSharp.Tools;

public class ToolExecutor : IToolExecutor
{
    private readonly GlobalToolRegistry _registry;
    private readonly string _workingDirectory;

    public ToolExecutor(GlobalToolRegistry registry, string? workingDirectory = null)
    {
        _registry = registry;
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
    }

    public async Task<ToolResult> ExecuteAsync(string toolName, string input, CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonInput = JsonSerializer.Deserialize<JsonElement>(input);
            var result = await ExecuteToolAsync(toolName, jsonInput, cancellationToken);
            return new ToolResult(result);
        }
        catch (Exception ex)
        {
            return new ToolResult(ex.Message, true);
        }
    }
    
    private async Task<string> ExecuteToolAsync(string toolName, JsonElement input, CancellationToken cancellationToken)
    {
        return toolName switch
        {
            "bash" => await ExecuteBashAsync(input, cancellationToken),
            "read_file" => await ExecuteReadFileAsync(input, cancellationToken),
            "write_file" => await ExecuteWriteFileAsync(input, cancellationToken),
            "edit_file" => await ExecuteEditFileAsync(input, cancellationToken),
            "glob_search" => ExecuteGlobSearch(input),
            "grep_search" => ExecuteGrepSearch(input),
            "WebFetch" => await ExecuteWebFetchAsync(input, cancellationToken),
            "WebSearch" => await ExecuteWebSearchAsync(input, cancellationToken),
            "TodoWrite" => ExecuteTodoWrite(input),
            "Skill" => ExecuteSkill(input),
            "Agent" => ExecuteAgent(input),
            "ToolSearch" => ExecuteToolSearch(input),
            "NotebookEdit" => ExecuteNotebookEdit(input),
            "Sleep" => ExecuteSleep(input),
            "SendUserMessage" => ExecuteSendUserMessage(input),
            "Config" => ExecuteConfig(input),
            "StructuredOutput" => ExecuteStructuredOutput(input),
            "REPL" => await ExecuteReplAsync(input, cancellationToken),
            "PowerShell" => await ExecutePowerShellAsync(input, cancellationToken),
            _ => throw new ArgumentException($"Unsupported tool: {toolName}")
        };
    }
    
    private async Task<string> ExecuteBashAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var command = input.GetProperty("command").GetString() ?? string.Empty;
        var timeout = input.TryGetProperty("timeout", out var timeoutEl) 
            ? TimeSpan.FromMilliseconds(timeoutEl.GetInt64())
            : TimeSpan.FromSeconds(120);
        var description = input.TryGetProperty("description", out var descEl) 
            ? descEl.GetString() 
            : null;
        
        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/bash",
            Arguments = OperatingSystem.IsWindows() ? $"/c {command}" : $"-c \"{command}\"",
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        using var process = new Process { StartInfo = startInfo };
        process.Start();
        
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            return JsonSerializer.Serialize(new { error = "Command timed out", stdout, stderr });
        }
        
        return JsonSerializer.Serialize(new { stdout, stderr, exitCode = process.ExitCode });
    }
    
    private async Task<string> ExecuteReadFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var offset = input.TryGetProperty("offset", out var offsetEl) ? offsetEl.GetInt32() : 0;
        var limit = input.TryGetProperty("limit", out var limitEl) ? limitEl.GetInt32() : 2000;
        
        var fullPath = Path.GetFullPath(path, _workingDirectory);
        var lines = await File.ReadAllLinesAsync(fullPath, cancellationToken);
        
        var selectedLines = lines.Skip(offset).Take(limit);
        var result = selectedLines.Select((line, i) => $"{offset + i + 1}: {line}");
        
        return JsonSerializer.Serialize(new { path, lines = result.ToList() });
    }
    
    private async Task<string> ExecuteWriteFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var content = input.GetProperty("content").GetString() ?? string.Empty;
        
        var fullPath = Path.GetFullPath(path, _workingDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        
        return JsonSerializer.Serialize(new { path, linesWritten = content.Split('\n').Length });
    }
    
    private async Task<string> ExecuteEditFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var oldString = input.GetProperty("old_string").GetString() ?? string.Empty;
        var newString = input.GetProperty("new_string").GetString() ?? string.Empty;
        var replaceAll = input.TryGetProperty("replace_all", out var replaceAllEl) && replaceAllEl.GetBoolean();
        
        var fullPath = Path.GetFullPath(path, _workingDirectory);
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        
        if (replaceAll)
        {
            content = content.Replace(oldString, newString);
        }
        else
        {
            var idx = content.IndexOf(oldString);
            if (idx < 0)
                throw new ArgumentException("old_string not found in file");
            content = content.Remove(idx, oldString.Length).Insert(idx, newString);
        }
        
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        
        return JsonSerializer.Serialize(new { path, edited = true });
    }
    
    private string ExecuteGlobSearch(JsonElement input)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? string.Empty;
        var basePath = input.TryGetProperty("path", out var pathEl)
            ? pathEl.GetString() ?? _workingDirectory
            : _workingDirectory;

        var fullPath = Path.GetFullPath(basePath, _workingDirectory);
        var files = Directory
            .EnumerateFiles(fullPath, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(fullPath, file).Replace('\\', '/'))
            .Where(relativePath => GlobMatches(relativePath, pattern))
            .ToList();

        return JsonSerializer.Serialize(new { pattern, files });
    }
    
    private string ExecuteGrepSearch(JsonElement input)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? string.Empty;
        var basePath = input.TryGetProperty("path", out var pathEl) 
            ? pathEl.GetString() ?? _workingDirectory 
            : _workingDirectory;
        var caseInsensitive = input.TryGetProperty("i", out var iEl) && iEl.GetBoolean();
        
        var options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
        var regex = new Regex(pattern, options);
        
        var fullPath = Path.GetFullPath(basePath, _workingDirectory);
        var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
            .Where(f => !f.Contains(".git") && !f.Contains("node_modules"));
        
        var matches = new List<GrepMatch>();
        
        foreach (var file in files)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        matches.Add(new GrepMatch(
                            Path.GetRelativePath(fullPath, file),
                            i + 1,
                            lines[i].Trim()
                        ));
                    }
                }
            }
            catch
            {
            }
        }
        
        return JsonSerializer.Serialize(new { pattern, matches });
    }
    
    private async Task<string> ExecuteWebFetchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var url = input.GetProperty("url").GetString() ?? string.Empty;
        var prompt = input.GetProperty("prompt").GetString() ?? string.Empty;
        
        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        
        var response = await client.GetAsync(url, cancellationToken);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        
        return JsonSerializer.Serialize(new
        {
            url,
            statusCode = (int)response.StatusCode,
            content = content.Length > 1000 ? content[..1000] + "..." : content
        });
    }
    
    private async Task<string> ExecuteWebSearchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var query = input.GetProperty("query").GetString() ?? string.Empty;
        
        using var client = new HttpClient();
        var searchUrl = $"https://html.duckduckgo.com/html/?q={Uri.EscapeDataString(query)}";
        
        var response = await client.GetStringAsync(searchUrl, cancellationToken);
        
        return JsonSerializer.Serialize(new { query, results = new[] { "Web search not fully implemented" } });
    }
    
    private string ExecuteTodoWrite(JsonElement input)
    {
        var todos = input.GetProperty("todos").EnumerateArray()
            .Select(t => new TodoItem(
                t.GetProperty("content").GetString() ?? string.Empty,
                t.GetProperty("activeForm").GetString() ?? string.Empty,
                t.GetProperty("status").GetString() ?? "pending"
            ))
            .ToList();
        
        var storePath = Path.Combine(_workingDirectory, ".codesharp-todos.json");
        File.WriteAllText(storePath, JsonSerializer.Serialize(todos));
        
        return JsonSerializer.Serialize(new { todos, stored = true });
    }
    
    private string ExecuteSkill(JsonElement input)
    {
        var skill = input.GetProperty("skill").GetString() ?? string.Empty;
        var args = input.TryGetProperty("args", out var argsEl) ? argsEl.GetString() : null;
        
        return JsonSerializer.Serialize(new { skill, args, status = "Skill loading not fully implemented" });
    }
    
    private string ExecuteAgent(JsonElement input)
    {
        var description = input.GetProperty("description").GetString() ?? string.Empty;
        var prompt = input.GetProperty("prompt").GetString() ?? string.Empty;
        
        return JsonSerializer.Serialize(new
        {
            agentId = Guid.NewGuid().ToString(),
            description,
            status = "Agent execution not fully implemented"
        });
    }
    
    private string ExecuteToolSearch(JsonElement input)
    {
        var query = input.GetProperty("query").GetString() ?? string.Empty;
        var maxResults = input.TryGetProperty("max_results", out var maxEl) ? maxEl.GetInt32() : 5;
        
        return JsonSerializer.Serialize(new { query, matches = Array.Empty<string>() });
    }
    
    private string ExecuteNotebookEdit(JsonElement input)
    {
        return JsonSerializer.Serialize(new { status = "NotebookEdit not fully implemented" });
    }
    
    private string ExecuteSleep(JsonElement input)
    {
        var durationMs = input.GetProperty("duration_ms").GetInt64();
        Thread.Sleep((int)durationMs);
        return JsonSerializer.Serialize(new { durationMs, completed = true });
    }
    
    private string ExecuteSendUserMessage(JsonElement input)
    {
        var message = input.GetProperty("message").GetString() ?? string.Empty;
        Console.WriteLine(message);
        return JsonSerializer.Serialize(new { message, sent = true });
    }
    
    private string ExecuteConfig(JsonElement input)
    {
        return JsonSerializer.Serialize(new { status = "Config tool not fully implemented" });
    }
    
    private string ExecuteStructuredOutput(JsonElement input)
    {
        return JsonSerializer.Serialize(new { structuredOutput = input });
    }

    private Task<string> ExecuteReplAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var code = input.GetProperty("code").GetString() ?? string.Empty;
        var language = input.GetProperty("language").GetString() ?? "python";

        return Task.FromResult(JsonSerializer.Serialize(new
        {
            language,
            stdout = "",
            stderr = "REPL not fully implemented",
            exitCode = 1
        }));
    }
    
    private async Task<string> ExecutePowerShellAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var command = input.GetProperty("command").GetString() ?? string.Empty;
        
        if (!OperatingSystem.IsWindows())
        {
            return JsonSerializer.Serialize(new { error = "PowerShell only available on Windows" });
        }
        
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command {command}",
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        
        using var process = Process.Start(startInfo);
        if (process is null)
            return JsonSerializer.Serialize(new { error = "Failed to start PowerShell" });
        
        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        
        return JsonSerializer.Serialize(new { stdout, stderr, exitCode = process.ExitCode });
    }

    private static bool GlobMatches(string relativePath, string pattern)
    {
        var normalizedPath = relativePath.Replace('\\', '/');
        var normalizedPattern = (pattern ?? string.Empty).Trim().Replace('\\', '/');

        if (string.IsNullOrEmpty(normalizedPattern) || normalizedPattern == "*")
        {
            return true;
        }

        if (!normalizedPattern.Contains('/'))
        {
            return WildcardMatch(Path.GetFileName(normalizedPath), normalizedPattern);
        }

        var regexPattern = Regex.Escape(normalizedPattern)
            .Replace(@"\*\*/", @"(?:.*/)?")
            .Replace(@"/\*\*", @"(?:/.*)?")
            .Replace(@"\*\*", @".*")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]");

        return Regex.IsMatch(normalizedPath, $"^{regexPattern}$", RegexOptions.IgnoreCase);
    }

    private static bool WildcardMatch(string input, string pattern)
    {
        var regexPattern = Regex.Escape(pattern)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".");

        return Regex.IsMatch(input, $"^{regexPattern}$", RegexOptions.IgnoreCase);
    }
}

internal record GrepMatch(string File, int Line, string Content);
internal record TodoItem(string Content, string ActiveForm, string Status);
