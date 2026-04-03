using System.Text.RegularExpressions;

namespace CodeSharp.Tools;

internal static class WorkspaceSymbolSearch
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".c",
        ".h",
        ".hpp",
        ".hh",
        ".cpp",
        ".cc",
        ".cxx",
        ".js",
        ".jsx",
        ".ts",
        ".tsx",
        ".mjs",
        ".py",
        ".html",
        ".htm"
    };

    private static readonly Regex NamespaceRegex = new(
        @"^\s*namespace\s+([A-Za-z_][A-Za-z0-9_\.]*)",
        RegexOptions.Compiled
    );

    private static readonly Regex CSharpTypeRegex = new(
        @"^\s*(?:\[[^\]]+\]\s*)*(?:(?:public|internal|protected|private|file|static|sealed|abstract|partial|readonly|unsafe|new)\s+)*(class|record|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled
    );

    private static readonly Regex CSharpMethodRegex = new(
        @"^\s*(?:\[[^\]]+\]\s*)*(?:(?:public|internal|protected|private|static|virtual|override|abstract|async|sealed|partial|extern|unsafe|new)\s+)+(?:[\w<>\[\]\?\.]+\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled
    );

    private static readonly Regex CSharpPropertyRegex = new(
        @"^\s*(?:\[[^\]]+\]\s*)*(?:(?:public|internal|protected|private|static|virtual|override|abstract|sealed|partial|required|new)\s+)+(?:[\w<>\[\]\?\.]+\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=>|\{)",
        RegexOptions.Compiled
    );

    private static readonly Regex CSharpFieldRegex = new(
        @"^\s*(?:\[[^\]]+\]\s*)*(?:(?:public|internal|protected|private|static|readonly|volatile|const|required|new)\s+)+(?:[\w<>\[\]\?\.]+\s+)+([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|;)",
        RegexOptions.Compiled
    );

    private static readonly Regex CppAltTypeRegex = new(
        @"^\s*(class|struct|enum|union)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled
    );

    private static readonly Regex CppFunctionRegex = new(
        @"^\s*(?:inline\s+|static\s+|constexpr\s+|virtual\s+|explicit\s+|friend\s+|extern\s+|consteval\s+|constinit\s+)*(?:[\w:<>~\*&]+\s+)+([A-Za-z_~][A-Za-z0-9_:~]*)\s*\([^;]*\)\s*(?:const)?\s*(?:noexcept)?\s*(?:\{|;)?\s*$",
        RegexOptions.Compiled
    );

    private static readonly Regex CppMacroRegex = new(
        @"^\s*#define\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled
    );

    private static readonly Regex JsTypeRegex = new(
        @"^\s*(?:export\s+)?(?:default\s+)?(class|interface|type|enum)\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled
    );

    private static readonly Regex JsFunctionRegex = new(
        @"^\s*(?:export\s+)?(?:default\s+)?function\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled
    );

    private static readonly Regex JsVariableFunctionRegex = new(
        @"^\s*(?:export\s+)?(?:const|let|var)\s+([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?:async\s*)?(?:function\b|\([^)]*\)\s*=>|[A-Za-z_][A-Za-z0-9_]*\s*=>)",
        RegexOptions.Compiled
    );

    private static readonly Regex PythonClassRegex = new(
        @"^\s*class\s+([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Compiled
    );

    private static readonly Regex PythonFunctionRegex = new(
        @"^\s*(?:async\s+def|def)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Compiled
    );

    private static readonly Regex HtmlIdRegex = new(
        @"\bid\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex HtmlClassRegex = new(
        @"\bclass\s*=\s*[""']([^""']+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly Regex HtmlCustomTagRegex = new(
        @"<([a-z][a-z0-9\-]*-[a-z0-9\-]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase
    );

    private static readonly HashSet<string> ExcludedMethodNames = new(StringComparer.Ordinal)
    {
        "if",
        "for",
        "foreach",
        "while",
        "switch",
        "catch",
        "using",
        "lock",
        "return",
        "nameof"
    };

    public static bool IsSupportedSourceFile(string path) =>
        SourceExtensions.Contains(Path.GetExtension(path));

    public static SymbolSearchResult FindSymbols(
        IEnumerable<string> files,
        string rootPath,
        string query,
        string? kind,
        int limit,
        string? matchType
    )
    {
        var definitions = EnumerateDefinitions(files, rootPath)
            .Where(symbol => Matches(symbol.Name, query, matchType))
            .Where(symbol => string.IsNullOrWhiteSpace(kind) || symbol.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .OrderBy(symbol => Score(symbol, query, matchType))
            .ThenBy(symbol => symbol.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(symbol => symbol.Line)
            .ToList();

        return new SymbolSearchResult(
            query,
            kind,
            definitions.Count,
            definitions.Take(limit).ToList(),
            definitions.Count > limit
        );
    }

    public static SymbolReferenceSearchResult FindReferences(
        IEnumerable<string> files,
        string rootPath,
        string symbol,
        int limit,
        bool includeDeclarations
    )
    {
        var definitions = EnumerateDefinitions(files, rootPath)
            .Where(definition => definition.Name.Equals(symbol, StringComparison.Ordinal))
            .ToList();

        var declarationSet = definitions
            .Select(definition => (definition.File, definition.Line, definition.Column))
            .ToHashSet();

        var pattern = BuildReferencePattern(symbol);
        var references = new List<SymbolReference>();

        foreach (var file in files.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
        {
            var relativePath = NormalizeRelativePath(rootPath, file);
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                foreach (Match match in pattern.Matches(lines[lineIndex]))
                {
                    var isDeclaration = declarationSet.Contains((relativePath, lineIndex + 1, match.Index + 1));
                    if (isDeclaration && !includeDeclarations)
                    {
                        continue;
                    }

                    references.Add(new SymbolReference(
                        relativePath,
                        lineIndex + 1,
                        match.Index + 1,
                        lines[lineIndex].Trim(),
                        isDeclaration
                    ));
                }
            }
        }

        return new SymbolReferenceSearchResult(
            symbol,
            definitions,
            references.Count,
            references.Take(limit).ToList(),
            references.Count > limit
        );
    }

    private static IEnumerable<SymbolDefinition> EnumerateDefinitions(IEnumerable<string> files, string rootPath)
    {
        foreach (var file in files.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase))
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }

            var relativePath = NormalizeRelativePath(rootPath, file);
            var extension = Path.GetExtension(file);
            foreach (var definition in extension.ToLowerInvariant() switch
            {
                ".cs" => EnumerateCSharpDefinitions(lines, relativePath),
                ".c" or ".h" or ".hpp" or ".hh" or ".cpp" or ".cc" or ".cxx" => EnumerateCppDefinitions(lines, relativePath),
                ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" => EnumerateJsDefinitions(lines, relativePath, extension),
                ".py" => EnumeratePythonDefinitions(lines, relativePath),
                ".html" or ".htm" => EnumerateHtmlDefinitions(lines, relativePath),
                _ => Array.Empty<SymbolDefinition>()
            })
            {
                yield return definition;
            }
        }
    }

    private static IEnumerable<SymbolDefinition> EnumerateCSharpDefinitions(IReadOnlyList<string> lines, string file)
    {
        string? currentNamespace = null;
        var typeStack = new Stack<(string Name, int BraceDepth)>();
        var braceDepth = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                braceDepth += CountBraceDelta(line);
                PruneContainerStack(typeStack, braceDepth);
                continue;
            }

            var declarationBraceDepth = braceDepth;

            if (TryMatch(NamespaceRegex, line, out var namespaceMatch))
            {
                currentNamespace = namespaceMatch.Groups[1].Value;
                yield return BuildDefinition(namespaceMatch.Groups[1].Value, "namespace", "csharp", file, lineIndex + 1, line, currentNamespace, typeStack);
            }

            if (TryMatch(CSharpTypeRegex, line, out var typeMatch))
            {
                var kind = typeMatch.Groups[1].Value.ToLowerInvariant();
                var name = typeMatch.Groups[2].Value;
                yield return BuildDefinition(name, kind, "csharp", file, lineIndex + 1, line, currentNamespace, typeStack);
                typeStack.Push((name, declarationBraceDepth));
            }
            else if (TryMatch(CSharpMethodRegex, line, out var methodMatch))
            {
                var name = methodMatch.Groups[1].Value;
                if (!ExcludedMethodNames.Contains(name))
                {
                    yield return BuildDefinition(name, "method", "csharp", file, lineIndex + 1, line, currentNamespace, typeStack);
                }
            }
            else if (TryMatch(CSharpPropertyRegex, line, out var propertyMatch) &&
                     (trimmed.Contains(" get", StringComparison.Ordinal) ||
                      trimmed.Contains(" get;", StringComparison.Ordinal) ||
                      trimmed.Contains(" set", StringComparison.Ordinal) ||
                      trimmed.Contains(" init", StringComparison.Ordinal) ||
                      trimmed.Contains("=>", StringComparison.Ordinal)))
            {
                yield return BuildDefinition(propertyMatch.Groups[1].Value, "property", "csharp", file, lineIndex + 1, line, currentNamespace, typeStack);
            }
            else if (TryMatch(CSharpFieldRegex, line, out var fieldMatch))
            {
                yield return BuildDefinition(fieldMatch.Groups[1].Value, "field", "csharp", file, lineIndex + 1, line, currentNamespace, typeStack);
            }

            braceDepth += CountBraceDelta(line);
            PruneContainerStack(typeStack, braceDepth);
        }
    }

    private static IEnumerable<SymbolDefinition> EnumerateCppDefinitions(IReadOnlyList<string> lines, string file)
    {
        string? currentNamespace = null;
        var typeStack = new Stack<(string Name, int BraceDepth)>();
        var braceDepth = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                braceDepth += CountBraceDelta(line);
                PruneContainerStack(typeStack, braceDepth);
                continue;
            }

            var declarationBraceDepth = braceDepth;

            if (TryMatch(NamespaceRegex, line, out var namespaceMatch))
            {
                currentNamespace = namespaceMatch.Groups[1].Value;
                yield return BuildDefinition(namespaceMatch.Groups[1].Value, "namespace", "cpp", file, lineIndex + 1, line, currentNamespace, typeStack);
            }

            if (TryMatch(CppAltTypeRegex, line, out var typeMatch))
            {
                var kind = typeMatch.Groups[1].Value.ToLowerInvariant();
                var name = typeMatch.Groups[2].Value;
                yield return BuildDefinition(name, kind, "cpp", file, lineIndex + 1, line, currentNamespace, typeStack);
                typeStack.Push((name, declarationBraceDepth));
            }
            else if (TryMatch(CppMacroRegex, line, out var macroMatch))
            {
                yield return BuildDefinition(macroMatch.Groups[1].Value, "macro", "cpp", file, lineIndex + 1, line, currentNamespace, typeStack);
            }
            else if (TryMatch(CppFunctionRegex, line, out var functionMatch))
            {
                var name = functionMatch.Groups[1].Value.Split("::").Last();
                if (!ExcludedMethodNames.Contains(name))
                {
                    yield return BuildDefinition(name, "function", "cpp", file, lineIndex + 1, line, currentNamespace, typeStack);
                }
            }

            braceDepth += CountBraceDelta(line);
            PruneContainerStack(typeStack, braceDepth);
        }
    }

    private static IEnumerable<SymbolDefinition> EnumerateJsDefinitions(IReadOnlyList<string> lines, string file, string extension)
    {
        var language = extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
                       extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase)
            ? "javascript"
            : "typescript";

        string? currentNamespace = null;
        var typeStack = new Stack<(string Name, int BraceDepth)>();
        var braceDepth = 0;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                braceDepth += CountBraceDelta(line);
                PruneContainerStack(typeStack, braceDepth);
                continue;
            }

            var declarationBraceDepth = braceDepth;
            if (TryMatch(JsTypeRegex, line, out var typeMatch))
            {
                var kind = typeMatch.Groups[1].Value.ToLowerInvariant() switch
                {
                    "type" => "type_alias",
                    _ => typeMatch.Groups[1].Value.ToLowerInvariant()
                };
                var name = typeMatch.Groups[2].Value;
                yield return BuildDefinition(name, kind, language, file, lineIndex + 1, line, currentNamespace, typeStack);
                if (kind is "class" or "interface" or "enum")
                {
                    typeStack.Push((name, declarationBraceDepth));
                }
            }
            else if (TryMatch(JsFunctionRegex, line, out var functionMatch))
            {
                yield return BuildDefinition(functionMatch.Groups[1].Value, "function", language, file, lineIndex + 1, line, currentNamespace, typeStack);
            }
            else if (TryMatch(JsVariableFunctionRegex, line, out var variableMatch))
            {
                yield return BuildDefinition(variableMatch.Groups[1].Value, "function", language, file, lineIndex + 1, line, currentNamespace, typeStack);
            }

            braceDepth += CountBraceDelta(line);
            PruneContainerStack(typeStack, braceDepth);
        }
    }

    private static IEnumerable<SymbolDefinition> EnumeratePythonDefinitions(IReadOnlyList<string> lines, string file)
    {
        string? currentClass = null;
        var classIndent = -1;

        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            var indent = line.TakeWhile(static c => c == ' ' || c == '\t').Count();
            if (currentClass is not null && indent <= classIndent && !trimmed.StartsWith("def ", StringComparison.Ordinal) && !trimmed.StartsWith("async def ", StringComparison.Ordinal))
            {
                currentClass = null;
                classIndent = -1;
            }

            if (TryMatch(PythonClassRegex, line, out var classMatch))
            {
                currentClass = classMatch.Groups[1].Value;
                classIndent = indent;
                yield return new SymbolDefinition(
                    file,
                    lineIndex + 1,
                    Math.Max(1, line.IndexOf(currentClass, StringComparison.Ordinal) + 1),
                    currentClass,
                    "class",
                    null,
                    null,
                    "python",
                    line.Trim()
                );
            }
            else if (TryMatch(PythonFunctionRegex, line, out var functionMatch))
            {
                yield return new SymbolDefinition(
                    file,
                    lineIndex + 1,
                    Math.Max(1, line.IndexOf(functionMatch.Groups[1].Value, StringComparison.Ordinal) + 1),
                    functionMatch.Groups[1].Value,
                    "function",
                    currentClass,
                    null,
                    "python",
                    line.Trim()
                );
            }
        }
    }

    private static IEnumerable<SymbolDefinition> EnumerateHtmlDefinitions(IReadOnlyList<string> lines, string file)
    {
        for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
        {
            var line = lines[lineIndex];

            foreach (Match match in HtmlIdRegex.Matches(line))
            {
                yield return new SymbolDefinition(
                    file,
                    lineIndex + 1,
                    match.Groups[1].Index + 1,
                    match.Groups[1].Value,
                    "html_id",
                    null,
                    null,
                    "html",
                    line.Trim()
                );
            }

            foreach (Match match in HtmlClassRegex.Matches(line))
            {
                foreach (var className in match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    yield return new SymbolDefinition(
                        file,
                        lineIndex + 1,
                        Math.Max(1, line.IndexOf(className, StringComparison.Ordinal) + 1),
                        className,
                        "html_class",
                        null,
                        null,
                        "html",
                        line.Trim()
                    );
                }
            }

            foreach (Match match in HtmlCustomTagRegex.Matches(line))
            {
                yield return new SymbolDefinition(
                    file,
                    lineIndex + 1,
                    match.Groups[1].Index + 1,
                    match.Groups[1].Value,
                    "custom_tag",
                    null,
                    null,
                    "html",
                    line.Trim()
                );
            }
        }
    }

    private static SymbolDefinition BuildDefinition(
        string name,
        string kind,
        string language,
        string file,
        int lineNumber,
        string line,
        string? currentNamespace,
        Stack<(string Name, int BraceDepth)> containerStack
    )
    {
        var container = containerStack.Count > 0 ? containerStack.Peek().Name : currentNamespace;
        var column = Math.Max(1, line.IndexOf(name, StringComparison.Ordinal) + 1);
        return new SymbolDefinition(file, lineNumber, column, name, kind, container, currentNamespace, language, line.Trim());
    }

    private static void PruneContainerStack(Stack<(string Name, int BraceDepth)> stack, int braceDepth)
    {
        while (stack.Count > 0 && braceDepth <= stack.Peek().BraceDepth)
        {
            stack.Pop();
        }
    }

    private static int CountBraceDelta(string line) =>
        line.Count(static c => c == '{') - line.Count(static c => c == '}');

    private static bool Matches(string candidate, string query, string? matchType)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var comparison = StringComparison.OrdinalIgnoreCase;
        return (matchType ?? "contains").ToLowerInvariant() switch
        {
            "exact" => candidate.Equals(query, comparison),
            "prefix" => candidate.StartsWith(query, comparison),
            _ => candidate.Contains(query, comparison)
        };
    }

    private static int Score(SymbolDefinition symbol, string query, string? matchType)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return 3;
        }

        if (symbol.Name.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (symbol.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return string.Equals(matchType, "contains", StringComparison.OrdinalIgnoreCase) ? 2 : 3;
    }

    private static Regex BuildReferencePattern(string symbol)
    {
        if (Regex.IsMatch(symbol, @"^[A-Za-z_][A-Za-z0-9_]*$"))
        {
            return new Regex($@"\b{Regex.Escape(symbol)}\b", RegexOptions.Compiled);
        }

        return new Regex(Regex.Escape(symbol), RegexOptions.Compiled);
    }

    private static string NormalizeRelativePath(string rootPath, string file) =>
        Path.GetRelativePath(rootPath, file).Replace('\\', '/');

    private static bool TryMatch(Regex regex, string line, out Match match)
    {
        match = regex.Match(line);
        return match.Success;
    }
}

internal sealed record SymbolDefinition(
    string File,
    int Line,
    int Column,
    string Name,
    string Kind,
    string? Container,
    string? Namespace,
    string Language,
    string Context
);

internal sealed record SymbolSearchResult(
    string Query,
    string? Kind,
    int TotalMatches,
    IReadOnlyList<SymbolDefinition> Matches,
    bool Truncated
);

internal sealed record SymbolReference(
    string File,
    int Line,
    int Column,
    string Context,
    bool IsDeclaration
);

internal sealed record SymbolReferenceSearchResult(
    string Symbol,
    IReadOnlyList<SymbolDefinition> Definitions,
    int TotalReferences,
    IReadOnlyList<SymbolReference> References,
    bool Truncated
);
