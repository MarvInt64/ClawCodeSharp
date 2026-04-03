using CodeSharp.Api;
using CodeSharp.Core;

namespace CodeSharp.Cli;

internal static class ProviderAccessWorkflow
{
    internal readonly record struct ApiKeyResolution(string ApiKey, bool Prompted);

    public static GlobalSettings SynchronizeModelSelection(GlobalSettings settings, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return settings.WithModel(null).WithProvider(null);
        }

        var resolvedModel = ModelAliases.ResolveModelAlias(model.Trim());
        var provider = ProviderDetection.DetectProviderKind(resolvedModel);
        return settings
            .WithModel(resolvedModel)
            .WithProvider(ProviderParsing.NormalizeProvider(provider));
    }

    public static ProviderKind ResolveProviderKind(string model, ProviderKind? explicitProvider = null) =>
        explicitProvider ?? ProviderDetection.DetectProviderKind(ModelAliases.ResolveModelAlias(model));

    public static string DescribeProviderAccess(GlobalSettings settings)
    {
        var model = settings.Model;
        var provider = !string.IsNullOrWhiteSpace(model)
            ? ProviderDetection.DetectProviderKind(ModelAliases.ResolveModelAlias(model))
            : settings.GetProviderKind();

        if (provider is null)
        {
            return "no provider selected";
        }

        var providerName = ProviderParsing.NormalizeProvider(provider.Value);
        return $"{providerName} · {DescribeApiKeyStatus(settings, provider.Value)}";
    }

    public static string DescribeApiKeyStatus(GlobalSettings settings, ProviderKind provider)
    {
        if (!string.IsNullOrWhiteSpace(settings.GetApiKey(provider)))
        {
            return "stored key";
        }

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GetApiKeyEnvVar(provider)))
            ? $"env {GetApiKeyEnvVar(provider)}"
            : "missing key";
    }

    public static string? GetAvailableApiKey(GlobalSettings settings, ProviderKind provider) =>
        !string.IsNullOrWhiteSpace(settings.GetApiKey(provider))
            ? settings.GetApiKey(provider)
            : Environment.GetEnvironmentVariable(GetApiKeyEnvVar(provider));

    public static ApiKeyResolution EnsureApiKeyAvailable(
        GlobalSettings settings,
        ProviderKind provider,
        string? model = null,
        IReadOnlyList<string>? headerLines = null
    )
    {
        if (GetAvailableApiKey(settings, provider) is { Length: > 0 } apiKey)
        {
            return new ApiKeyResolution(apiKey, false);
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException(BuildMissingApiKeyMessage(provider, model));
        }

        var providerName = ProviderParsing.NormalizeProvider(provider);
        var footer = "Press Enter to save an API key now, or Esc to cancel.";
        var body = model is null
            ? $"No API key is configured for `{providerName}`.\nNeither a stored key nor `{GetApiKeyEnvVar(provider)}` was found."
            : $"The selected model `{model}` uses `{providerName}`.\nNeither a stored key nor `{GetApiKeyEnvVar(provider)}` was found.";

        ClearHeader(headerLines);
        Console.WriteLine(ConsoleUi.MessageBlock("missing api key", body, footer));

        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Escape)
            {
                throw new InvalidOperationException(BuildMissingApiKeyMessage(provider, model));
            }

            if (key.Key != ConsoleKey.Enter)
            {
                continue;
            }

            ClearHeader(headerLines);
            Console.WriteLine(ConsoleUi.MessageBlock(
                "api key",
                $"Enter API key for `{providerName}`.\nIt will be stored in `~/.codesharp/settings.json`.",
                $"Environment alternative: {GetApiKeyEnvVar(provider)}"
            ));
            var entered = ReadSecret($"API key for {providerName}: ");
            if (string.IsNullOrWhiteSpace(entered))
            {
                throw new InvalidOperationException(BuildMissingApiKeyMessage(provider, model));
            }

            return new ApiKeyResolution(entered.Trim(), true);
        }
    }

    public static string BuildMissingApiKeyMessage(ProviderKind provider, string? model = null)
    {
        var providerName = ProviderParsing.NormalizeProvider(provider);
        var envVar = GetApiKeyEnvVar(provider);
        return model is null
            ? $"Missing API key for {providerName}. Set it with `codesharp config set api-key {providerName} <value>` or export `{envVar}`."
            : $"The model `{model}` requires provider `{providerName}`, but no API key is configured. Use `codesharp config set api-key {providerName} <value>` or export `{envVar}`.";
    }

    private static string GetApiKeyEnvVar(ProviderKind provider) => provider switch
    {
        ProviderKind.CodeSharpApi => "ANTHROPIC_API_KEY",
        ProviderKind.OpenAi => "OPENAI_API_KEY",
        ProviderKind.Xai => "XAI_API_KEY",
        ProviderKind.Nvidia => "NVIDIA_API_KEY",
        _ => "API_KEY"
    };

    private static void ClearHeader(IReadOnlyList<string>? headerLines)
    {
        try
        {
            Console.Clear();
        }
        catch
        {
        }

        if (headerLines is null)
        {
            return;
        }

        foreach (var line in headerLines)
        {
            Console.WriteLine(line);
        }

        if (headerLines.Count > 0)
        {
            Console.WriteLine();
        }
    }

    private static string? ReadSecret(string prompt)
    {
        try
        {
            Console.Write(prompt);
            var secret = new System.Text.StringBuilder();

            while (true)
            {
                var key = Console.ReadKey(intercept: true);
                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return secret.ToString();
                }

                if (key.Key == ConsoleKey.Backspace)
                {
                    if (secret.Length > 0)
                    {
                        secret.Length--;
                    }
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    secret.Append(key.KeyChar);
                }
            }
        }
        catch
        {
            return Console.ReadLine();
        }
    }
}
