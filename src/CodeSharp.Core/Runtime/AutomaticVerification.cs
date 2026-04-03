using System.Text.Json;

namespace CodeSharp.Core;

public enum AutoVerifyMode
{
    Off,
    DangerOnly,
    On
}

public static class AutoVerifyModeExtensions
{
    public static string AsString(this AutoVerifyMode mode) => mode switch
    {
        AutoVerifyMode.Off => "off",
        AutoVerifyMode.DangerOnly => "danger-only",
        AutoVerifyMode.On => "on",
        _ => "danger-only"
    };

    public static AutoVerifyMode FromString(string value) => value.Trim().ToLowerInvariant() switch
    {
        "off" => AutoVerifyMode.Off,
        "danger-only" => AutoVerifyMode.DangerOnly,
        "danger_only" => AutoVerifyMode.DangerOnly,
        "danger" => AutoVerifyMode.DangerOnly,
        "on" => AutoVerifyMode.On,
        "true" => AutoVerifyMode.On,
        _ => throw new ArgumentException($"Unknown auto-verify mode: {value}")
    };

    public static bool TryParse(string? value, out AutoVerifyMode mode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            mode = AutoVerifyMode.DangerOnly;
            return false;
        }

        try
        {
            mode = FromString(value);
            return true;
        }
        catch
        {
            mode = AutoVerifyMode.DangerOnly;
            return false;
        }
    }
}

internal sealed record AutoVerifyPlan(
    string Command,
    string Description,
    string Strategy,
    IReadOnlyList<string> MutatedPaths
);

internal static class AutoVerifyPlanner
{
    public static AutoVerifyPlan? TryCreate(string workingDirectory, IReadOnlyCollection<string> mutatedPaths)
    {
        var normalizedPaths = mutatedPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalizedPaths.Count == 0)
        {
            return null;
        }

        if (HasDotNetMarkers(workingDirectory) && normalizedPaths.Any(IsDotNetSource))
        {
            return new AutoVerifyPlan(
                "dotnet build",
                "Verify the .NET workspace after code edits.",
                ".NET build",
                normalizedPaths
            );
        }

        if (HasRustMarkers(workingDirectory) && normalizedPaths.Any(IsRustSource))
        {
            return new AutoVerifyPlan(
                "cargo check",
                "Verify the Rust workspace after code edits.",
                "Rust check",
                normalizedPaths
            );
        }

        if (TryCreateNodePlan(workingDirectory, normalizedPaths) is { } nodePlan)
        {
            return nodePlan;
        }

        if (TryCreatePythonPlan(workingDirectory, normalizedPaths) is { } pythonPlan)
        {
            return pythonPlan;
        }

        return null;
    }

    private static AutoVerifyPlan? TryCreateNodePlan(string workingDirectory, IReadOnlyCollection<string> mutatedPaths)
    {
        if (!File.Exists(Path.Combine(workingDirectory, "package.json")) ||
            !mutatedPaths.Any(IsNodeSource))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(workingDirectory, "package.json")));
            if (!document.RootElement.TryGetProperty("scripts", out var scripts) ||
                scripts.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? scriptName = null;
            if (scripts.TryGetProperty("typecheck", out var typecheck) &&
                typecheck.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(typecheck.GetString()))
            {
                scriptName = "typecheck";
            }
            else if (scripts.TryGetProperty("build", out var build) &&
                     build.ValueKind == JsonValueKind.String &&
                     !string.IsNullOrWhiteSpace(build.GetString()))
            {
                scriptName = "build";
            }

            if (scriptName is null)
            {
                return null;
            }

            var packageManager = DetectPackageManager(workingDirectory);
            var command = packageManager switch
            {
                "pnpm" => $"pnpm run {scriptName}",
                "yarn" => $"yarn {scriptName}",
                "bun" => $"bun run {scriptName}",
                _ => $"npm run {scriptName}"
            };

            return new AutoVerifyPlan(
                command,
                $"Verify the JavaScript/TypeScript workspace with `{scriptName}` after code edits.",
                $"Node {scriptName}",
                mutatedPaths.ToList()
            );
        }
        catch
        {
            return null;
        }
    }

    private static AutoVerifyPlan? TryCreatePythonPlan(string workingDirectory, IReadOnlyCollection<string> mutatedPaths)
    {
        if (!HasPythonMarkers(workingDirectory))
        {
            return null;
        }

        var changedPythonFiles = mutatedPaths
            .Where(path => string.Equals(Path.GetExtension(path), ".py", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (changedPythonFiles.Count == 0)
        {
            return null;
        }

        var command = $"python -m py_compile {string.Join(' ', changedPythonFiles.Select(ShellQuote))}";
        return new AutoVerifyPlan(
            command,
            "Syntax-check the changed Python files after code edits.",
            "Python syntax check",
            changedPythonFiles
        );
    }

    private static bool HasDotNetMarkers(string workingDirectory) =>
        Directory.EnumerateFiles(workingDirectory, "*.sln", SearchOption.TopDirectoryOnly).Any() ||
        Directory.EnumerateFiles(workingDirectory, "*.csproj", SearchOption.AllDirectories).Any() ||
        Directory.EnumerateFiles(workingDirectory, "*.fsproj", SearchOption.AllDirectories).Any();

    private static bool HasRustMarkers(string workingDirectory) =>
        File.Exists(Path.Combine(workingDirectory, "Cargo.toml"));

    private static bool HasPythonMarkers(string workingDirectory) =>
        File.Exists(Path.Combine(workingDirectory, "pyproject.toml")) ||
        File.Exists(Path.Combine(workingDirectory, "setup.py")) ||
        File.Exists(Path.Combine(workingDirectory, "pytest.ini")) ||
        File.Exists(Path.Combine(workingDirectory, "requirements.txt"));

    private static bool IsDotNetSource(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".props", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".targets", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".sln", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRustSource(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".rs", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(path), "Cargo.toml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsNodeSource(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".js", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jsx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ts", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".tsx", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".mjs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cjs", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".css", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".scss", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".html", StringComparison.OrdinalIgnoreCase);
    }

    private static string DetectPackageManager(string workingDirectory)
    {
        if (File.Exists(Path.Combine(workingDirectory, "pnpm-lock.yaml")))
        {
            return "pnpm";
        }

        if (File.Exists(Path.Combine(workingDirectory, "yarn.lock")))
        {
            return "yarn";
        }

        if (File.Exists(Path.Combine(workingDirectory, "bun.lockb")) ||
            File.Exists(Path.Combine(workingDirectory, "bun.lock")))
        {
            return "bun";
        }

        return "npm";
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
