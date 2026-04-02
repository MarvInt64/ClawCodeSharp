using Claw.Api.Providers;

namespace Claw.Api;

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
        return provider switch
        {
            ProviderKind.ClawApi => new ProviderClient(
                ClawApiClient.FromEnv(),
                ProviderKind.ClawApi
            ),
            ProviderKind.Xai => new ProviderClient(
                OpenAiCompatClient.FromEnv(OpenAiCompatConfig.Xai()),
                ProviderKind.Xai
            ),
            ProviderKind.OpenAi => new ProviderClient(
                OpenAiCompatClient.FromEnv(OpenAiCompatConfig.OpenAi()),
                ProviderKind.OpenAi
            ),
            ProviderKind.Nvidia => new ProviderClient(
                OpenAiCompatClient.FromEnv(OpenAiCompatConfig.Nvidia()),
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
        _ => model
    };

    public static ProviderKind DetectProviderKind(string model) => model.ToLowerInvariant() switch
    {
        var m when m.StartsWith("grok") => ProviderKind.Xai,
        var m when m.StartsWith("kimi") || m.Contains("moonshotai") => ProviderKind.Nvidia,
        _ => ProviderKind.ClawApi
    };
}
