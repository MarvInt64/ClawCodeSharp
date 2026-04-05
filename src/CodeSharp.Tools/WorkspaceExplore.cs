using System.Text.RegularExpressions;

namespace CodeSharp.Tools;

internal sealed record ExploreTaskRecord(
    string TaskId,
    string Description,
    string Prompt,
    string SubagentType,
    string Status,
    int TotalFiles,
    string Summary,
    IReadOnlyList<ExploreLanguageStat> Languages,
    IReadOnlyList<ExploreDirectoryStat> Directories,
    IReadOnlyList<ExploreFileHint> SuggestedFiles,
    IReadOnlyList<ExploreKeywordHit> KeywordHits,
    string CreatedAtUtc
);

internal sealed record ExploreLanguageStat(string Language, int Files);
internal sealed record ExploreDirectoryStat(string Directory, int Files);
internal sealed record ExploreFileHint(string File, string Reason);
internal sealed record ExploreKeywordHit(string Keyword, string File, int Line, string Context);

internal static class WorkspaceExplore
{
    private static readonly HashSet<string> StopWords =
    [
        "the", "and", "that", "this", "with", "from", "into", "about", "would", "could", "should", "there",
        "their", "them", "they", "what", "when", "where", "which", "while", "your", "yours", "please", "make",
        "build", "create", "update", "project", "codebase", "code", "repo", "repository", "implement", "change"
    ];

    public static ExploreTaskRecord Analyze(
        string taskId,
        string rootPath,
        string description,
        string prompt,
        IReadOnlyList<string> files
    )
    {
        var languages = files
            .GroupBy(DetectLanguage, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ExploreLanguageStat(group.Key, group.Count()))
            .OrderByDescending(stat => stat.Files)
            .ThenBy(stat => stat.Language, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var directories = files
            .Select(file => GetTopLevelDirectory(rootPath, file))
            .GroupBy(dir => dir, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ExploreDirectoryStat(group.Key, group.Count()))
            .OrderByDescending(stat => stat.Files)
            .ThenBy(stat => stat.Directory, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var suggestedFiles = RankRepresentativeFiles(rootPath, files)
            .Take(10)
            .ToList();

        var keywordHits = FindKeywordHits(rootPath, files, ExtractKeywords($"{description} {prompt}"))
            .Take(12)
            .ToList();

        var summary = BuildSummary(rootPath, files.Count, languages, directories, suggestedFiles, keywordHits);

        return new ExploreTaskRecord(
            taskId,
            description,
            prompt,
            "Explore",
            "completed",
            files.Count,
            summary,
            languages,
            directories,
            suggestedFiles,
            keywordHits,
            DateTime.UtcNow.ToString("O")
        );
    }

    private static string BuildSummary(
        string rootPath,
        int totalFiles,
        IReadOnlyList<ExploreLanguageStat> languages,
        IReadOnlyList<ExploreDirectoryStat> directories,
        IReadOnlyList<ExploreFileHint> suggestedFiles,
        IReadOnlyList<ExploreKeywordHit> keywordHits
    )
    {
        var summaryParts = new List<string>
        {
            $"Workspace scan covered {totalFiles:N0} candidate source/config files."
        };

        if (languages.Count > 0)
        {
            summaryParts.Add("Top languages: " + string.Join(", ", languages.Take(5).Select(stat => $"{stat.Language} ({stat.Files})")));
        }

        if (directories.Count > 0)
        {
            summaryParts.Add("Top directories: " + string.Join(", ", directories.Take(5).Select(stat => $"{stat.Directory} ({stat.Files})")));
        }

        if (suggestedFiles.Count > 0)
        {
            summaryParts.Add("Suggested starting points: " + string.Join(", ", suggestedFiles.Take(5).Select(file => file.File)));
        }

        if (keywordHits.Count > 0)
        {
            var focus = keywordHits
                .GroupBy(hit => hit.Keyword, StringComparer.OrdinalIgnoreCase)
                .Select(group => $"{group.Key} ({group.Count()} hits)")
                .ToList();
            summaryParts.Add("Prompt-related keywords: " + string.Join(", ", focus));
        }

        return string.Join(" ", summaryParts);
    }

    private static IEnumerable<ExploreFileHint> RankRepresentativeFiles(string rootPath, IReadOnlyList<string> files)
    {
        foreach (var candidate in files
                     .Select(file => new RankedHint(new ExploreFileHint(file, DescribeRepresentativeReason(file)), ScoreRepresentativeFile(rootPath, file)))
                     .Where(item => item.Score > 0)
                     .OrderByDescending(item => item.Score)
                     .ThenBy(item => item.Hint.File, StringComparer.OrdinalIgnoreCase)
                     .Select(item => item.Hint))
        {
            yield return candidate with { File = GetRelativePath(rootPath, candidate.File) };
        }
    }

    private static IEnumerable<ExploreKeywordHit> FindKeywordHits(
        string rootPath,
        IReadOnlyList<string> files,
        IReadOnlyList<string> keywords
    )
    {
        if (keywords.Count == 0)
        {
            yield break;
        }

        foreach (var file in files.Take(200))
        {
            string? text = null;
            foreach (var keyword in keywords)
            {
                if (file.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    yield return new ExploreKeywordHit(keyword, GetRelativePath(rootPath, file), 0, "matched file path");
                    continue;
                }

                text ??= SafeReadText(file);
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var lines = text.Replace("\r\n", "\n").Split('\n');
                for (var index = 0; index < lines.Length; index++)
                {
                    if (!lines[index].Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    yield return new ExploreKeywordHit(keyword, GetRelativePath(rootPath, file), index + 1, lines[index].Trim());
                    break;
                }
            }
        }
    }

    private static IReadOnlyList<string> ExtractKeywords(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z0-9_\-\.]{4,}")
            .Select(match => match.Value)
            .Where(token => !StopWords.Contains(token))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

    private static string DetectLanguage(string file)
    {
        var extension = Path.GetExtension(file).ToLowerInvariant();
        return extension switch
        {
            ".cs" or ".csproj" or ".sln" or ".props" or ".targets" => "csharp",
            ".ts" or ".tsx" => "typescript",
            ".js" or ".jsx" or ".mjs" or ".cjs" => "javascript",
            ".py" => "python",
            ".cpp" or ".cc" or ".cxx" or ".hpp" or ".h" or ".c" => "cpp",
            ".rs" => "rust",
            ".go" => "go",
            ".java" => "java",
            ".html" => "html",
            ".css" or ".scss" => "css",
            ".json" or ".yml" or ".yaml" or ".toml" or ".xml" => "config",
            ".md" => "markdown",
            _ => extension.TrimStart('.')
        };
    }

    private static string GetTopLevelDirectory(string rootPath, string file)
    {
        var relative = GetRelativePath(rootPath, file);
        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return ".";
        }

        return segments[0];
    }

    private static int ScoreRepresentativeFile(string rootPath, string file)
    {
        var relative = GetRelativePath(rootPath, file).ToLowerInvariant();
        var name = Path.GetFileName(relative);
        var score = 0;

        if (relative.StartsWith("src/", StringComparison.Ordinal)) score += 10;
        if (relative.StartsWith("app/", StringComparison.Ordinal)) score += 8;
        if (relative.StartsWith("lib/", StringComparison.Ordinal)) score += 6;
        if (relative.StartsWith("tests/", StringComparison.Ordinal) || relative.Contains("/test", StringComparison.Ordinal)) score += 5;
        if (name is "program.cs" or "main.ts" or "main.js" or "app.ts" or "app.js" or "app.py" or "server.ts" or "server.js") score += 20;
        if (name.Contains("readme", StringComparison.OrdinalIgnoreCase)) score += 6;
        if (name.Contains("config", StringComparison.OrdinalIgnoreCase) || name is "package.json" or "pyproject.toml" or "cargo.toml") score += 12;
        if (name.Contains("service", StringComparison.OrdinalIgnoreCase) || name.Contains("controller", StringComparison.OrdinalIgnoreCase) || name.Contains("context", StringComparison.OrdinalIgnoreCase)) score += 8;

        return score;
    }

    private static string DescribeRepresentativeReason(string file)
    {
        var name = Path.GetFileName(file);
        if (name.Equals("Program.cs", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("main.ts", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("main.js", StringComparison.OrdinalIgnoreCase))
        {
            return "entry point";
        }

        if (name.Equals("package.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("pyproject.toml", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("Cargo.toml", StringComparison.OrdinalIgnoreCase))
        {
            return "project config";
        }

        if (name.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            return "tests";
        }

        return "representative source file";
    }

    private static string GetRelativePath(string rootPath, string file)
    {
        try
        {
            return Path.GetRelativePath(rootPath, file).Replace('\\', '/');
        }
        catch
        {
            return file.Replace('\\', '/');
        }
    }

    private static string? SafeReadText(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > 64 * 1024)
            {
                return null;
            }

            return File.ReadAllText(file);
        }
        catch
        {
            return null;
        }
    }

    private sealed record RankedHint(ExploreFileHint Hint, int Score);
}
