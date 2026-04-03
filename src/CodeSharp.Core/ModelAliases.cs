namespace CodeSharp.Core;

public static class ModelAliases
{
    public const string DefaultModel = "moonshotai/kimi-k2.5";
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

    public static int EstimatedContextWindowForModel(string model)
    {
        var normalized = model.ToLowerInvariant();
        return normalized switch
        {
            _ when normalized.Contains("gpt-5") => 200_000,
            _ when normalized.Contains("gpt-4.1") => 200_000,
            _ when normalized.Contains("o3") => 200_000,
            _ when normalized.Contains("claude") => 180_000,
            _ when normalized.Contains("kimi") => 128_000,
            _ when normalized.Contains("glm") => 128_000,
            _ => 128_000
        };
    }
}
