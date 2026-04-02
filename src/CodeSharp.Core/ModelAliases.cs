namespace CodeSharp.Core;

public static class ModelAliases
{
    public const string DefaultModel = "claude-opus-4-6";
    public const string DefaultDate = "2026-03-31";
    
    public static string ResolveModelAlias(string model) => model.ToLowerInvariant() switch
    {
        "opus" => "claude-opus-4-6",
        "sonnet" => "claude-sonnet-4-6",
        "haiku" => "claude-haiku-4-5-20251213",
        "glm5" => "z-ai/glm5",
        _ => model
    };
    
    public static int MaxTokensForModel(string model) =>
        model.Contains("opus") ? 32_000 : 64_000;
}
