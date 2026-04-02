namespace CodeSharp.Api;

public enum ProviderKind
{
    CodeSharpApi,
    OpenAi,
    Xai,
    Nvidia,
}

public static class ProviderDetection
{
    public static ProviderKind DetectProviderKind(string model) => model.ToLowerInvariant() switch
    {
        var m when m.StartsWith("grok") => ProviderKind.Xai,
        var m when m.StartsWith("kimi") || m.Contains("moonshotai") => ProviderKind.Nvidia,
        _ => ProviderKind.CodeSharpApi
    };
    
    public static string ResolveModelAlias(string model) => model.ToLowerInvariant() switch
    {
        "opus" => "claude-opus-4-6",
        "sonnet" => "claude-sonnet-4-6",
        "haiku" => "claude-haiku-4-5-20251213",
        "grok" => "grok-3",
        "grok-mini" => "grok-3-mini",
        _ => model
    };
}
