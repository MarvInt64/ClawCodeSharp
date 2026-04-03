using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using CodeSharp.Core;

namespace CodeSharp.Tools;

public class ToolExecutor : IToolExecutor
{
    private const int DefaultReadFileLimit = 250;

    private readonly GlobalToolRegistry _registry;
    private readonly string _workingDirectory;
    private readonly GitIgnoreMatcher _gitIgnoreMatcher;

    public ToolExecutor(GlobalToolRegistry registry, string? workingDirectory = null)
    {
        _registry = registry;
        _workingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        _gitIgnoreMatcher = GitIgnoreMatcher.Load(_workingDirectory);
    }

    public async Task<ToolResult> ExecuteAsync(string toolName, string input, CancellationToken cancellationToken = default)
    {
        try
        {
            var jsonInput = JsonSerializer.Deserialize<JsonElement>(input);
            var result = await ExecuteToolAsync(toolName, jsonInput, cancellationToken);
            return new ToolResult(result, IsToolErrorResult(result));
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

        if (TryBuildReadFileRedirectError(command, out var redirectError))
        {
            return JsonSerializer.Serialize(new { error = redirectError });
        }
        
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
        var offset = input.TryGetProperty("offset", out var offsetEl) ? Math.Max(0, offsetEl.GetInt32()) : 0;
        var limit = input.TryGetProperty("limit", out var limitEl) ? Math.Max(1, limitEl.GetInt32()) : DefaultReadFileLimit;
        
        var fullPath = Path.GetFullPath(path, _workingDirectory);
        var result = new List<string>(Math.Min(limit, DefaultReadFileLimit));
        var lineIndex = 0;
        var hasMore = false;

        using var stream = File.OpenRead(fullPath);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (lineIndex >= offset)
            {
                if (result.Count < limit)
                {
                    result.Add($"{lineIndex + 1}: {line}");
                }
                else
                {
                    hasMore = true;
                    break;
                }
            }

            lineIndex++;
        }

        var linesRead = result.Count;
        var startLine = linesRead == 0 ? 0 : offset + 1;
        var endLine = linesRead == 0 ? 0 : offset + linesRead;

        return JsonSerializer.Serialize(new
        {
            path,
            offset,
            limit,
            startLine,
            endLine,
            lines = result,
            hasMore,
            nextOffset = hasMore ? offset + linesRead : (int?)null
        });
    }
    
    private async Task<string> ExecuteWriteFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var content = input.GetProperty("content").GetString() ?? string.Empty;
        
        var fullPath = Path.GetFullPath(path, _workingDirectory);
        var previousContent = File.Exists(fullPath)
            ? await File.ReadAllTextAsync(fullPath, cancellationToken)
            : null;
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        var preview = BuildDiffPreview(previousContent, content);
        
        return JsonSerializer.Serialize(new
        {
            path,
            linesWritten = content.Split('\n').Length,
            preview = preview.Lines,
            previewTruncated = preview.Truncated
        });
    }
    
    private async Task<string> ExecuteEditFileAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var path = input.GetProperty("path").GetString() ?? string.Empty;
        var oldString = input.GetProperty("old_string").GetString() ?? string.Empty;
        var newString = input.GetProperty("new_string").GetString() ?? string.Empty;
        var replaceAll = input.TryGetProperty("replace_all", out var replaceAllEl) && replaceAllEl.GetBoolean();
        
        var fullPath = Path.GetFullPath(path, _workingDirectory);
        var content = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var originalContent = content;
        
        if (replaceAll)
        {
            if (!content.Contains(oldString, StringComparison.Ordinal))
            {
                throw new ArgumentException(BuildEditMismatchMessage(path, content, oldString));
            }

            content = content.Replace(oldString, newString);
        }
        else
        {
            var idx = content.IndexOf(oldString, StringComparison.Ordinal);
            if (idx < 0)
                throw new ArgumentException(BuildEditMismatchMessage(path, content, oldString));
            content = content.Remove(idx, oldString.Length).Insert(idx, newString);
        }
        
        await File.WriteAllTextAsync(fullPath, content, cancellationToken);
        var preview = BuildDiffPreview(originalContent, content);
        
        return JsonSerializer.Serialize(new
        {
            path,
            edited = true,
            preview = preview.Lines,
            previewTruncated = preview.Truncated
        });
    }

    private static string BuildEditMismatchMessage(string path, string fileContent, string oldString)
    {
        // Find the first substantial line of old_string in the file to give the model useful context
        var firstLine = oldString.Split('\n')
            .Select(static l => l.Trim())
            .FirstOrDefault(l => l.Length > 8);

        if (firstLine is not null)
        {
            var idx = fileContent.IndexOf(firstLine, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var linesBefore = fileContent[..idx].Split('\n');
                var anchorLineNum = linesBefore.Length; // 1-based
                var allLines = fileContent.Split('\n');
                var startLine = Math.Max(0, anchorLineNum - 3);
                var contextLines = allLines.Skip(startLine).Take(20).ToList();

                var context = new System.Text.StringBuilder();
                for (var i = 0; i < contextLines.Count; i++)
                {
                    context.AppendLine($"{startLine + i + 1,5}: {contextLines[i]}");
                }

                return $"old_string not found in file `{path}`. " +
                       $"The first line of your old_string was found at line {anchorLineNum} but the surrounding content did not match. " +
                       $"Use this exact content as your new old_string:\n```\n{context.ToString().TrimEnd()}\n```\n" +
                       "If you cannot make a focused edit, use write_file to replace the entire method instead.";
            }

            return $"old_string not found in file `{path}` — " +
                   $"the distinctive line `{firstLine[..Math.Min(60, firstLine.Length)]}` does not appear in the file. " +
                   "Re-read the file with read_file and use an exact snippet from the current content. " +
                   "If the method body has changed significantly, use write_file to replace it entirely.";
        }

        return $"old_string not found in file `{path}`. Re-read this file with read_file and retry with an exact snippet from the current file.";
    }
    
    private string ExecuteGlobSearch(JsonElement input)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? string.Empty;
        var basePath = input.TryGetProperty("path", out var pathEl)
            ? pathEl.GetString() ?? _workingDirectory
            : _workingDirectory;
        var limit = input.TryGetProperty("limit", out var limitEl) ? limitEl.GetInt32() : 200;

        var fullPath = Path.GetFullPath(basePath, _workingDirectory);
        var files = EnumerateSearchFiles(fullPath)
            .Select(file => Path.GetRelativePath(fullPath, file).Replace('\\', '/'))
            .Where(relativePath => GlobMatches(relativePath, pattern))
            .ToList();

        return JsonSerializer.Serialize(new
        {
            pattern,
            totalFiles = files.Count,
            files = files.Take(limit).ToList(),
            truncated = files.Count > limit
        });
    }
    
    private string ExecuteGrepSearch(JsonElement input)
    {
        var pattern = input.GetProperty("pattern").GetString() ?? string.Empty;
        var basePath = input.TryGetProperty("path", out var pathEl) 
            ? pathEl.GetString() ?? _workingDirectory 
            : _workingDirectory;
        var glob = input.TryGetProperty("glob", out var globEl)
            ? globEl.GetString()
            : null;
        var caseInsensitive = !input.TryGetProperty("i", out var iEl) || iEl.GetBoolean();
        var limit = input.TryGetProperty("limit", out var limitEl) ? limitEl.GetInt32() : 200;
        
        var contextLines = input.TryGetProperty("context_lines", out var ctxEl) ? ctxEl.GetInt32() : 2;
        var options = caseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
        var regex = new Regex(pattern, options);

        var fullPath = Path.GetFullPath(basePath, _workingDirectory);
        var files = EnumerateSearchFiles(fullPath)
            .Where(file =>
            {
                if (string.IsNullOrWhiteSpace(glob))
                {
                    return true;
                }

                var relativePath = Path.GetRelativePath(fullPath, file).Replace('\\', '/');
                return GlobMatches(relativePath, glob);
            });

        var matches = new List<GrepMatch>();
        var filesWithMatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalMatches = 0;

        foreach (var file in files)
        {
            try
            {
                var lines = File.ReadAllLines(file);
                var relativePath = Path.GetRelativePath(fullPath, file).Replace('\\', '/');
                for (var i = 0; i < lines.Length; i++)
                {
                    if (regex.IsMatch(lines[i]))
                    {
                        totalMatches++;
                        filesWithMatches.Add(relativePath);
                        if (matches.Count < limit)
                        {
                            var before = contextLines > 0
                                ? lines[Math.Max(0, i - contextLines)..i]
                                : [];
                            var after = contextLines > 0
                                ? lines[(i + 1)..Math.Min(lines.Length, i + 1 + contextLines)]
                                : [];
                            matches.Add(new GrepMatch(
                                relativePath,
                                i + 1,
                                lines[i],
                                before,
                                after
                            ));
                        }
                    }
                }
            }
            catch
            {
            }
        }
        
        return JsonSerializer.Serialize(new
        {
            pattern,
            glob,
            caseInsensitive,
            totalMatches,
            filesWithMatches = filesWithMatches.Count,
            matches,
            truncated = totalMatches > matches.Count
        });
    }

    private IEnumerable<string> EnumerateSearchFiles(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            var current = pending.Pop();

            IEnumerable<string> directories;
            try
            {
                directories = Directory.EnumerateDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories)
            {
                if (ShouldSkipSearchDirectory(directory))
                {
                    continue;
                }

                pending.Push(directory);
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(current);
            }
            catch
            {
                continue;
            }

            foreach (var file in files)
            {
                if (ShouldIgnoreSearchFile(file))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private bool ShouldSkipSearchDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".idea", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".vs", StringComparison.OrdinalIgnoreCase) ||
               _gitIgnoreMatcher.IsIgnored(path, isDirectory: true);
    }

    private bool ShouldIgnoreSearchFile(string path) =>
        _gitIgnoreMatcher.IsIgnored(path, isDirectory: false);
    
    private async Task<string> ExecuteWebFetchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var url = input.GetProperty("url").GetString() ?? string.Empty;

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(20);
        client.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");

        string rawHtml;
        int statusCode;
        try
        {
            var response = await client.GetAsync(url, cancellationToken);
            statusCode = (int)response.StatusCode;
            rawHtml = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { url, error = ex.Message });
        }

        var text = ExtractReadableText(rawHtml);
        const int maxChars = 8000;
        var truncated = text.Length > maxChars;

        return JsonSerializer.Serialize(new
        {
            url,
            statusCode,
            content = truncated ? text[..maxChars] + "\n[truncated]" : text,
            truncated
        });
    }

    private static string ExtractReadableText(string html)
    {
        // Remove <script>, <style>, <svg>, <head> blocks entirely
        var blockStrip = new Regex(@"<(script|style|svg|head|noscript|nav|footer|header)[^>]*>.*?</\1>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var cleaned = blockStrip.Replace(html, " ");

        // Strip remaining tags
        var tagStrip = new Regex(@"<[^>]+>", RegexOptions.Singleline);
        cleaned = tagStrip.Replace(cleaned, " ");

        // Decode HTML entities
        cleaned = WebUtility.HtmlDecode(cleaned);

        // Collapse whitespace
        cleaned = Regex.Replace(cleaned, @"[ \t]+", " ");
        cleaned = Regex.Replace(cleaned, @"\n\s*\n\s*\n+", "\n\n");

        return cleaned.Trim();
    }
    
    private async Task<string> ExecuteWebSearchAsync(JsonElement input, CancellationToken cancellationToken)
    {
        var query = input.GetProperty("query").GetString() ?? string.Empty;
        var allowedDomains = ParseStringList(input, "allowed_domains");
        var blockedDomains = ParseStringList(input, "blocked_domains");

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (compatible)");

        // DDG Lite is designed for programmatic access — no JS, no bot protection, instant response
        string html;
        try
        {
            var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query)]);
            var response = await client.PostAsync("https://lite.duckduckgo.com/lite/", content, cancellationToken);
            html = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { query, error = $"Search request failed: {ex.Message}" });
        }

        // DDG Lite structure: <a class="result-link" href="...">Title</a> followed by <td class="result-snippet">
        var tagStrip = new Regex(@"<[^>]+>", RegexOptions.Singleline);
        var linkRegex = new Regex(@"<a\s+class=""result-link""\s+href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var snippetRegex = new Regex(@"<td\s+class=""result-snippet""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var linkMatches = linkRegex.Matches(html);
        var snippetMatches = snippetRegex.Matches(html);
        var results = new List<object>();

        for (var i = 0; i < linkMatches.Count && results.Count < 8; i++)
        {
            var href = WebUtility.HtmlDecode(linkMatches[i].Groups[1].Value.Trim());
            if (!Uri.TryCreate(href, UriKind.Absolute, out var uri)) continue;
            if (uri.Scheme is not "http" and not "https") continue;

            var host = uri.Host.ToLowerInvariant();
            if (blockedDomains.Count > 0 && blockedDomains.Any(d => host == d || host.EndsWith("." + d))) continue;
            if (allowedDomains.Count > 0 && !allowedDomains.Any(d => host == d || host.EndsWith("." + d))) continue;

            var title = WebUtility.HtmlDecode(tagStrip.Replace(linkMatches[i].Groups[2].Value, "")).Trim();
            var snippet = i < snippetMatches.Count
                ? WebUtility.HtmlDecode(tagStrip.Replace(snippetMatches[i].Groups[1].Value, "")).Trim()
                : string.Empty;

            results.Add(new { title, url = uri.ToString(), snippet, domain = host });
        }

        return JsonSerializer.Serialize(new { query, resultCount = results.Count, results });
    }

    private static List<string> ParseStringList(JsonElement input, string property)
    {
        if (!input.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Array)
            return [];
        return el.EnumerateArray()
            .Where(static e => e.ValueKind == JsonValueKind.String)
            .Select(e => (e.GetString() ?? string.Empty).Trim().ToLowerInvariant())
            .Where(static s => s.Length > 0)
            .ToList();
    }
    
    private string ExecuteTodoWrite(JsonElement input)
    {
        if (!input.TryGetProperty("todos", out var todosElement) || todosElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("TodoWrite requires a `todos` array.");
        }

        var todos = todosElement.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(ParseTodoItem)
            .ToList();

        if (todos.Count == 0)
        {
            throw new InvalidOperationException("TodoWrite received no valid todo items.");
        }
        
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

    private static DiffPreview BuildDiffPreview(string? oldContent, string newContent, int maxPreviewLines = 12)
    {
        var oldLines = SplitLines(oldContent);
        var newLines = SplitLines(newContent);

        var prefix = 0;
        while (prefix < oldLines.Length &&
               prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var oldSuffix = oldLines.Length - 1;
        var newSuffix = newLines.Length - 1;
        while (oldSuffix >= prefix &&
               newSuffix >= prefix &&
               string.Equals(oldLines[oldSuffix], newLines[newSuffix], StringComparison.Ordinal))
        {
            oldSuffix--;
            newSuffix--;
        }

        var removed = oldSuffix >= prefix ? oldLines[prefix..(oldSuffix + 1)] : [];
        var added = newSuffix >= prefix ? newLines[prefix..(newSuffix + 1)] : [];

        if (removed.Length == 0 && added.Length == 0)
        {
            return new DiffPreview([], false);
        }

        var startLine = prefix + 1;
        var previewLines = new List<string>
        {
            $"@@ -{startLine},{removed.Length} +{startLine},{added.Length} @@"
        };

        previewLines.AddRange(removed.Select(static line => $"- {line}"));
        previewLines.AddRange(added.Select(static line => $"+ {line}"));

        var truncated = false;
        if (previewLines.Count > maxPreviewLines)
        {
            previewLines = [.. previewLines.Take(maxPreviewLines)];
            truncated = true;
        }

        return new DiffPreview(previewLines, truncated);
    }

    private static string[] SplitLines(string? content) =>
        string.IsNullOrEmpty(content)
            ? []
            : content.Replace("\r\n", "\n").Split('\n');

    private static TodoItem ParseTodoItem(JsonElement todo)
    {
        var content =
            JsonString(todo, "content") ??
            JsonString(todo, "text") ??
            JsonString(todo, "title") ??
            string.Empty;

        var activeForm =
            JsonString(todo, "activeForm") ??
            JsonString(todo, "active_form") ??
            JsonString(todo, "active") ??
            content;

        var status = NormalizeTodoStatus(
            JsonString(todo, "status") ??
            JsonString(todo, "state")
        );

        if (string.IsNullOrWhiteSpace(content) && string.IsNullOrWhiteSpace(activeForm))
        {
            throw new InvalidOperationException("Todo item requires at least `content` or `activeForm`.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            content = activeForm;
        }

        if (string.IsNullOrWhiteSpace(activeForm))
        {
            activeForm = content;
        }

        return new TodoItem(content, activeForm, status);
    }

    private static string NormalizeTodoStatus(string? status) =>
        status?.Trim().ToLowerInvariant() switch
        {
            "pending" => "pending",
            "in_progress" => "in_progress",
            "in-progress" => "in_progress",
            "active" => "in_progress",
            "completed" => "completed",
            "complete" => "completed",
            "done" => "completed",
            _ => "pending"
        };

    private static string? JsonString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool IsToolErrorResult(string output)
    {
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(output);
            if (json.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (json.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(error.GetString()))
            {
                return true;
            }

            if (json.TryGetProperty("exitCode", out var exitCode) &&
                exitCode.ValueKind == JsonValueKind.Number &&
                exitCode.TryGetInt32(out var code) &&
                code != 0)
            {
                return true;
            }

            if (json.TryGetProperty("status", out var status) &&
                status.ValueKind == JsonValueKind.String &&
                status.GetString()?.Contains("not fully implemented", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            if (json.TryGetProperty("stderr", out var stderr) &&
                stderr.ValueKind == JsonValueKind.String &&
                stderr.GetString()?.Contains("not fully implemented", StringComparison.OrdinalIgnoreCase) == true)
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private bool TryBuildReadFileRedirectError(string command, out string error)
    {
        error = string.Empty;
        var trimmed = command.Trim();
        if (!LooksLikeShellFileInspection(trimmed))
        {
            return false;
        }

        var path = ExtractWorkspacePathFromCommand(trimmed);
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var displayPath = path.Replace('\\', '/');
        error = $"Use read_file for file content inspection instead of bash on `{displayPath}`.";
        return true;
    }

    private static bool LooksLikeShellFileInspection(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        return command.Contains("cat ", StringComparison.Ordinal) ||
               command.Contains("sed -n", StringComparison.Ordinal) ||
               command.Contains("head ", StringComparison.Ordinal) ||
               command.Contains("tail ", StringComparison.Ordinal) ||
               command.Contains("od ", StringComparison.Ordinal) ||
               command.Contains("awk ", StringComparison.Ordinal) ||
               command.Contains("grep ", StringComparison.Ordinal) ||
               command.Contains("perl -ne", StringComparison.Ordinal);
    }

    private string? ExtractWorkspacePathFromCommand(string command)
    {
        foreach (var token in command.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = token.Trim().Trim('"', '\'', '(', ')');
            if (string.IsNullOrWhiteSpace(candidate) || candidate.StartsWith("-", StringComparison.Ordinal))
            {
                continue;
            }

            if (!LooksLikePath(candidate))
            {
                continue;
            }

            var fullPath = Path.IsPathRooted(candidate)
                ? Path.GetFullPath(candidate)
                : Path.GetFullPath(candidate, _workingDirectory);

            if (!fullPath.StartsWith(_workingDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (File.Exists(fullPath))
            {
                return Path.GetRelativePath(_workingDirectory, fullPath);
            }
        }

        return null;
    }

    private static bool LooksLikePath(string token) =>
        token.StartsWith("/", StringComparison.Ordinal) ||
        token.StartsWith("./", StringComparison.Ordinal) ||
        token.StartsWith("../", StringComparison.Ordinal) ||
        token.Contains('/') ||
        token.Contains('\\');
}

internal record GrepMatch(string File, int Line, string Content, string[] ContextBefore, string[] ContextAfter);
internal record TodoItem(string Content, string ActiveForm, string Status);
internal sealed record DiffPreview(IReadOnlyList<string> Lines, bool Truncated);

internal sealed class GitIgnoreMatcher
{
    private readonly string _rootPath;
    private readonly IReadOnlyList<GitIgnorePattern> _patterns;

    private GitIgnoreMatcher(string rootPath, IReadOnlyList<GitIgnorePattern> patterns)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _patterns = patterns;
    }

    public static GitIgnoreMatcher Load(string rootPath)
    {
        var gitIgnorePath = Path.Combine(rootPath, ".gitignore");
        if (!File.Exists(gitIgnorePath))
        {
            return new GitIgnoreMatcher(rootPath, []);
        }

        try
        {
            var patterns = File.ReadAllLines(gitIgnorePath)
                .Select(GitIgnorePattern.Parse)
                .Where(static pattern => pattern is not null)
                .Cast<GitIgnorePattern>()
                .ToList();

            return new GitIgnoreMatcher(rootPath, patterns);
        }
        catch
        {
            return new GitIgnoreMatcher(rootPath, []);
        }
    }

    public bool IsIgnored(string absolutePath, bool isDirectory)
    {
        if (_patterns.Count == 0)
        {
            return false;
        }

        var fullPath = Path.GetFullPath(absolutePath);
        if (!IsUnderRoot(fullPath))
        {
            return false;
        }

        var relativePath = Path.GetRelativePath(_rootPath, fullPath).Replace('\\', '/');
        if (relativePath.StartsWith("../", StringComparison.Ordinal))
        {
            return false;
        }

        var ignored = false;
        foreach (var pattern in _patterns)
        {
            if (!pattern.Matches(relativePath, isDirectory))
            {
                continue;
            }

            ignored = !pattern.Negated;
        }

        return ignored;
    }

    private bool IsUnderRoot(string fullPath)
    {
        if (string.Equals(fullPath, _rootPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = _rootPath.EndsWith(Path.DirectorySeparatorChar) || _rootPath.EndsWith(Path.AltDirectorySeparatorChar)
            ? _rootPath
            : _rootPath + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class GitIgnorePattern
{
    private readonly Regex _regex;

    private GitIgnorePattern(bool negated, bool directoryOnly, Regex regex)
    {
        Negated = negated;
        DirectoryOnly = directoryOnly;
        _regex = regex;
    }

    public bool Negated { get; }
    public bool DirectoryOnly { get; }

    public static GitIgnorePattern? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
        {
            return null;
        }

        var negated = trimmed.StartsWith('!');
        if (negated)
        {
            trimmed = trimmed[1..];
        }

        var directoryOnly = trimmed.EndsWith('/');
        if (directoryOnly)
        {
            trimmed = trimmed[..^1];
        }

        trimmed = trimmed.TrimStart('/').Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        var hasSlash = trimmed.Contains('/');
        var regexBody = BuildRegexBody(trimmed);
        var regexPattern = hasSlash
            ? $"^{regexBody}$"
            : $"(^|.*/){regexBody}$";

        if (directoryOnly)
        {
            regexPattern = hasSlash
                ? $"^{regexBody}(/.*)?$"
                : $"(^|.*/){regexBody}(/.*)?$";
        }

        return new GitIgnorePattern(
            negated,
            directoryOnly,
            new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)
        );
    }

    public bool Matches(string relativePath, bool isDirectory)
    {
        if (DirectoryOnly && !isDirectory)
        {
            return false;
        }

        return _regex.IsMatch(relativePath);
    }

    private static string BuildRegexBody(string pattern) =>
        Regex.Escape(pattern)
            .Replace(@"\*\*/", @"(?:.*/)?")
            .Replace(@"/\*\*", @"(?:/.*)?")
            .Replace(@"\*\*", @".*")
            .Replace(@"\*", @"[^/]*")
            .Replace(@"\?", @"[^/]");
}
