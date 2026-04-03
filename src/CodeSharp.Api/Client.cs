using CodeSharp.Api.Providers;

namespace CodeSharp.Api;

public class ProviderClient
{
    private readonly IProvider _provider;
    private readonly ProviderKind _providerKind;

    public ProviderKind ProviderKind => _providerKind;

    private ProviderClient(IProvider provider, ProviderKind kind)
    {
        _provider = provider;
        _providerKind = kind;
    }

    public static ProviderClient FromModel(string model)
    {
        return FromModelWithDefaultAuth(model, null);
    }

    public static ProviderClient FromProvider(ProviderKind provider)
    {
        return FromProvider(provider, null);
    }

    public static ProviderClient FromProvider(ProviderKind provider, string? apiKey)
    {
        return provider switch
        {
            ProviderKind.CodeSharpApi => new ProviderClient(
                string.IsNullOrWhiteSpace(apiKey) ? CodeSharpApiClient.FromEnv() : new CodeSharpApiClient(apiKey),
                ProviderKind.CodeSharpApi
            ),
            ProviderKind.Xai => new ProviderClient(
                string.IsNullOrWhiteSpace(apiKey)
                    ? OpenAiCompatClient.FromEnv(OpenAiCompatConfig.Xai())
                    : new OpenAiCompatClient(OpenAiCompatConfig.Xai() with { ApiKey = apiKey }),
                ProviderKind.Xai
            ),
            ProviderKind.OpenAi => new ProviderClient(
                string.IsNullOrWhiteSpace(apiKey)
                    ? OpenAiCompatClient.FromEnv(OpenAiCompatConfig.OpenAi())
                    : new OpenAiCompatClient(OpenAiCompatConfig.OpenAi() with { ApiKey = apiKey }),
                ProviderKind.OpenAi
            ),
            ProviderKind.Nvidia => new ProviderClient(
                string.IsNullOrWhiteSpace(apiKey)
                    ? OpenAiCompatClient.FromEnv(OpenAiCompatConfig.Nvidia())
                    : new OpenAiCompatClient(OpenAiCompatConfig.Nvidia() with { ApiKey = apiKey }),
                ProviderKind.Nvidia
            ),
            _ => throw new InvalidOperationException($"Unknown provider: {provider}")
        };
    }

    public static ProviderClient FromModelWithDefaultAuth(string model, string? defaultAuth)
    {
        var resolvedModel = ResolveModelAlias(model);
        var kind = DetectProviderKind(resolvedModel);
        return FromProvider(kind);
    }

    public async Task<MessageResponse> SendMessageAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        return await _provider.SendMessageAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<StreamEvent> StreamMessageAsync(MessageRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var ev in _provider.StreamMessageAsync(request, cancellationToken))
        {
            yield return ev;
        }
    }

    public static string ResolveModelAlias(string model) => model.ToLowerInvariant() switch
    {
        "opus" => "claude-opus-4-6",
        "sonnet" => "claude-sonnet-4-6",
        "haiku" => "claude-haiku-4-5-20251213",
        "grok" => "grok-3",
        "grok-mini" => "grok-3-mini",
        "glm5" => "z-ai/glm5",
        "kimi2.5" => "moonshotai/kimi-k2.5",
        _ => model
    };

    public static ProviderKind DetectProviderKind(string model) => model.ToLowerInvariant() switch
    {
        var m when m.StartsWith("grok") => ProviderKind.Xai,
        var m when m.StartsWith("kimi") || m.Contains("moonshotai") || m.StartsWith("z-ai/") => ProviderKind.Nvidia,
        var m when m.StartsWith("gpt-") || m.StartsWith("o1") || m.StartsWith("o3") || m.StartsWith("o4") => ProviderKind.OpenAi,
        _ => ProviderKind.CodeSharpApi
    };
}
