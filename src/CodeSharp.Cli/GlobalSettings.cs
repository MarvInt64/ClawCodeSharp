using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSharp.Api;
using CodeSharp.Core;

namespace CodeSharp.Cli;

internal sealed class GlobalSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _settingsPath;

    public GlobalSettingsStore(string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _settingsPath = Path.Combine(home, ".codesharp", "settings.json");
    }

    public string SettingsPath => _settingsPath;

    public GlobalSettings Load()
    {
        if (!File.Exists(_settingsPath))
        {
            return new GlobalSettings();
        }

        try
        {
            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<GlobalSettings>(json, JsonOptions) ?? new GlobalSettings();
        }
        catch
        {
            return new GlobalSettings();
        }
    }

    public void Save(GlobalSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
    }
}

internal sealed record GlobalSettings(
    string? Model = null,
    string? Provider = null,
    GlobalApiKeys? ApiKeys = null
)
{
    public string? GetApiKey(ProviderKind provider) => provider switch
    {
        ProviderKind.CodeSharpApi => ApiKeys?.Anthropic,
        ProviderKind.OpenAi => ApiKeys?.OpenAi,
        ProviderKind.Xai => ApiKeys?.Xai,
        ProviderKind.Nvidia => ApiKeys?.Nvidia,
        _ => null
    };

    public ProviderKind? GetProviderKind()
    {
        if (string.IsNullOrWhiteSpace(Provider))
        {
            return null;
        }

        return ProviderParsing.TryParseProvider(Provider, out var provider)
            ? provider
            : null;
    }

    public GlobalSettings WithModel(string? model) =>
        this with { Model = string.IsNullOrWhiteSpace(model) ? null : ModelAliases.ResolveModelAlias(model.Trim()) };

    public GlobalSettings WithProvider(string? provider) =>
        this with { Provider = string.IsNullOrWhiteSpace(provider) ? null : ProviderParsing.NormalizeProvider(provider) };

    public GlobalSettings WithApiKey(ProviderKind provider, string? apiKey)
    {
        var trimmed = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        var keys = (ApiKeys ?? new GlobalApiKeys()) with
        {
            Anthropic = provider == ProviderKind.CodeSharpApi ? trimmed : ApiKeys?.Anthropic,
            OpenAi = provider == ProviderKind.OpenAi ? trimmed : ApiKeys?.OpenAi,
            Xai = provider == ProviderKind.Xai ? trimmed : ApiKeys?.Xai,
            Nvidia = provider == ProviderKind.Nvidia ? trimmed : ApiKeys?.Nvidia
        };

        return this with { ApiKeys = keys };
    }
}

internal sealed record GlobalApiKeys(
    string? Anthropic = null,
    string? OpenAi = null,
    string? Xai = null,
    string? Nvidia = null
);

internal sealed record ConfigCommandResult(string Title, string Body, string? Footer = null);

internal static class ConfigCommandProcessor
{
    public static ConfigCommandResult Process(string? args, GlobalSettingsStore store)
    {
        var settings = store.Load();
        var tokens = Tokenize(args);

        if (tokens.Count == 0)
        {
            return RunInteractiveMenu(store);
        }

        if (tokens[0].Equals("show", StringComparison.OrdinalIgnoreCase))
        {
            return RenderSettings(settings, store.SettingsPath);
        }

        return tokens[0].ToLowerInvariant() switch
        {
            "path" => new ConfigCommandResult(
                "config",
                store.SettingsPath,
                "Global settings are stored in ~/.codesharp/settings.json."
            ),
            "set" => HandleSet(tokens.Skip(1).ToArray(), settings, store),
            "unset" => HandleUnset(tokens.Skip(1).ToArray(), settings, store),
            _ => RenderHelp(store.SettingsPath)
        };
    }

    private static ConfigCommandResult RunInteractiveMenu(GlobalSettingsStore store)
    {
        if (Console.IsInputRedirected)
        {
            return RenderSettings(store.Load(), store.SettingsPath);
        }

        while (true)
        {
            var settings = store.Load();
            var choice = RunMenu(
                "config",
                [
                    ("Provider", settings.Provider ?? "(not set)"),
                    ("Model", settings.Model ?? "(not set)"),
                    ("API keys", $"{CountConfiguredKeys(settings)}/4 configured"),
                    ("Clear values", "Remove defaults or stored keys"),
                    ("Show summary", "Inspect the current global config"),
                    ("Done", "Close config")
                ],
                "Use ↑/↓ to move, Enter to select, Esc to close.",
                allowEscape: true
            );

            switch (choice)
            {
                case 0:
                    SaveProviderFromMenu(store, settings);
                    break;
                case 1:
                    SaveModelFromMenu(store, settings);
                    break;
                case 2:
                    ManageApiKeysFromMenu(store, settings);
                    break;
                case 3:
                    ClearValueFromMenu(store, settings);
                    break;
                case 4:
                    PauseWithMessage("config", RenderSettings(settings, store.SettingsPath).Body, "Press any key to return.");
                    break;
                case 5:
                case -1:
                    Console.Clear();
                    return new ConfigCommandResult(
                        "config",
                        "Global settings updated.",
                        $"Stored in {store.SettingsPath}"
                    );
            }
        }
    }

    private static ConfigCommandResult HandleSet(
        IReadOnlyList<string> tokens,
        GlobalSettings settings,
        GlobalSettingsStore store
    )
    {
        if (tokens.Count < 2)
        {
            return RenderHelp(store.SettingsPath);
        }

        switch (tokens[0].ToLowerInvariant())
        {
            case "model":
            {
                var model = string.Join(' ', tokens.Skip(1));
                var updated = settings.WithModel(model);
                store.Save(updated);
                return new ConfigCommandResult(
                    "config",
                    $"Saved default model:\n{updated.Model}",
                    $"Stored in {store.SettingsPath}"
                );
            }
            case "provider":
            {
                var providerToken = tokens[1];
                if (!ProviderParsing.TryParseProvider(providerToken, out _))
                {
                    return new ConfigCommandResult("config", $"Unknown provider: {providerToken}");
                }

                var updated = settings.WithProvider(providerToken);
                store.Save(updated);
                return new ConfigCommandResult(
                    "config",
                    $"Saved default provider:\n{updated.Provider}",
                    $"Stored in {store.SettingsPath}"
                );
            }
            case "api-key":
            case "apikey":
            case "token":
            {
                var providerToken = tokens[1];
                if (!ProviderParsing.TryParseProvider(providerToken, out var provider))
                {
                    return new ConfigCommandResult("config", $"Unknown provider: {providerToken}");
                }

                var apiKey = tokens.Count > 2
                    ? string.Join(' ', tokens.Skip(2))
                    : ReadSecret($"Enter API key for {ProviderParsing.NormalizeProvider(provider)}: ");

                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    return new ConfigCommandResult("config", "No API key provided.");
                }

                var updated = settings.WithApiKey(provider, apiKey);
                store.Save(updated);
                return new ConfigCommandResult(
                    "config",
                    $"Saved API key for {ProviderParsing.NormalizeProvider(provider)}:\n{MaskSecret(updated.GetApiKey(provider))}",
                    $"Stored in {store.SettingsPath}"
                );
            }
            default:
                return RenderHelp(store.SettingsPath);
        }
    }

    private static ConfigCommandResult HandleUnset(
        IReadOnlyList<string> tokens,
        GlobalSettings settings,
        GlobalSettingsStore store
    )
    {
        if (tokens.Count == 0)
        {
            return RenderHelp(store.SettingsPath);
        }

        switch (tokens[0].ToLowerInvariant())
        {
            case "model":
            {
                var updated = settings.WithModel(null);
                store.Save(updated);
                return new ConfigCommandResult("config", "Removed default model.", $"Stored in {store.SettingsPath}");
            }
            case "provider":
            {
                var updated = settings.WithProvider(null);
                store.Save(updated);
                return new ConfigCommandResult("config", "Removed default provider.", $"Stored in {store.SettingsPath}");
            }
            case "api-key":
            case "apikey":
            case "token":
            {
                if (tokens.Count < 2 || !ProviderParsing.TryParseProvider(tokens[1], out var provider))
                {
                    return RenderHelp(store.SettingsPath);
                }

                var updated = settings.WithApiKey(provider, null);
                store.Save(updated);
                return new ConfigCommandResult(
                    "config",
                    $"Removed API key for {ProviderParsing.NormalizeProvider(provider)}.",
                    $"Stored in {store.SettingsPath}"
                );
            }
            default:
                return RenderHelp(store.SettingsPath);
        }
    }

    private static ConfigCommandResult RenderSettings(GlobalSettings settings, string settingsPath)
    {
        var body = new StringBuilder()
            .AppendLine("Global defaults")
            .AppendLine($"  Path             {settingsPath}")
            .AppendLine($"  Model            {settings.Model ?? "(not set)"}")
            .AppendLine($"  Provider         {settings.Provider ?? "(not set)"}")
            .AppendLine($"  Anthropic key    {MaskSecret(settings.ApiKeys?.Anthropic)}")
            .AppendLine($"  OpenAI key       {MaskSecret(settings.ApiKeys?.OpenAi)}")
            .AppendLine($"  xAI key          {MaskSecret(settings.ApiKeys?.Xai)}")
            .AppendLine($"  NVIDIA key       {MaskSecret(settings.ApiKeys?.Nvidia)}")
            .AppendLine()
            .AppendLine("Commands")
            .AppendLine("  codesharp config              Open guided menu")
            .AppendLine("  codesharp config set model <name>")
            .AppendLine("  codesharp config set provider <anthropic|openai|xai|nvidia>")
            .AppendLine("  codesharp config set api-key <provider> [value]")
            .AppendLine("  codesharp config unset model")
            .AppendLine("  codesharp config unset provider")
            .AppendLine("  codesharp config unset api-key <provider>");

        return new ConfigCommandResult("config", body.ToString().TrimEnd());
    }

    private static ConfigCommandResult RenderHelp(string settingsPath) => new(
        "config",
        $@"Config
  Path             {settingsPath}

Usage
  codesharp config
  codesharp config show
  codesharp config path
  codesharp config set model <name>
  codesharp config set provider <anthropic|openai|xai|nvidia>
  codesharp config set api-key <provider> [value]
  codesharp config unset model
  codesharp config unset provider
  codesharp config unset api-key <provider>"
    );

    private static List<string> Tokenize(string? args) =>
        string.IsNullOrWhiteSpace(args)
            ? []
            : args.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static string MaskSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(not set)";
        }

        if (value.Length <= 8)
        {
            return new string('*', value.Length);
        }

        return $"{value[..4]}…{value[^4..]}";
    }

    private static void SaveModelFromPrompt(GlobalSettingsStore store, GlobalSettings settings)
    {
        Console.Write("Default model (empty clears): ");
        var model = Console.ReadLine();
        store.Save(settings.WithModel(model));
    }

    private static void SaveModelFromMenu(GlobalSettingsStore store, GlobalSettings settings)
    {
        var choice = RunMenu(
            "model",
            [
                ("claude-opus-4-6", "Anthropic flagship"),
                ("claude-sonnet-4-6", "Anthropic balanced"),
                ("claude-haiku-4-5-20251213", "Anthropic fast"),
                ("gpt-5", "OpenAI flagship"),
                ("gpt-5-mini", "OpenAI smaller/faster"),
                ("grok-3", "xAI flagship"),
                ("grok-3-mini", "xAI smaller/faster"),
                ("z-ai/glm5", "NVIDIA / Z.ai"),
                ("Custom...", settings.Model ?? "Enter any model id"),
                ("Clear", "Remove stored default model")
            ],
            "Use ↑/↓ to move, Enter to select, Esc to go back.",
            allowEscape: true
        );

        switch (choice)
        {
            case 0:
                store.Save(settings.WithModel("claude-opus-4-6"));
                break;
            case 1:
                store.Save(settings.WithModel("claude-sonnet-4-6"));
                break;
            case 2:
                store.Save(settings.WithModel("claude-haiku-4-5-20251213"));
                break;
            case 3:
                store.Save(settings.WithModel("gpt-5"));
                break;
            case 4:
                store.Save(settings.WithModel("gpt-5-mini"));
                break;
            case 5:
                store.Save(settings.WithModel("grok-3"));
                break;
            case 6:
                store.Save(settings.WithModel("grok-3-mini"));
                break;
            case 7:
                store.Save(settings.WithModel("z-ai/glm5"));
                break;
            case 8:
                Console.Clear();
                SaveModelFromPrompt(store, settings);
                break;
            case 9:
                store.Save(settings.WithModel(null));
                break;
        }
    }

    private static void SaveProviderFromPrompt(GlobalSettingsStore store, GlobalSettings settings)
    {
        Console.WriteLine("Provider");
        Console.WriteLine("  1 anthropic");
        Console.WriteLine("  2 openai");
        Console.WriteLine("  3 xai");
        Console.WriteLine("  4 nvidia");
        Console.WriteLine("  0 clear");
        Console.Write("Select provider: ");
        var providerValue = Console.ReadLine()?.Trim();

        if (string.IsNullOrWhiteSpace(providerValue) || providerValue == "0")
        {
            store.Save(settings.WithProvider(null));
            return;
        }

        var normalized = providerValue switch
        {
            "1" => "anthropic",
            "2" => "openai",
            "3" => "xai",
            "4" => "nvidia",
            _ => providerValue
        };

        if (!ProviderParsing.TryParseProvider(normalized, out _))
        {
            Console.WriteLine(ConsoleUi.Warning($"Unknown provider: {providerValue}"));
            return;
        }

        store.Save(settings.WithProvider(normalized));
    }

    private static void SaveProviderFromMenu(GlobalSettingsStore store, GlobalSettings settings)
    {
        var choice = RunMenu(
            "provider",
            [
                ("anthropic", "Claude / Anthropic-compatible"),
                ("openai", "OpenAI-compatible chat completions"),
                ("xai", "xAI / Grok"),
                ("nvidia", "NVIDIA NIM / OpenAI-compatible"),
                ("Clear", "Remove stored default provider")
            ],
            "Use ↑/↓ to move, Enter to select, Esc to go back.",
            allowEscape: true
        );

        switch (choice)
        {
            case 0:
                store.Save(settings.WithProvider("anthropic"));
                break;
            case 1:
                store.Save(settings.WithProvider("openai"));
                break;
            case 2:
                store.Save(settings.WithProvider("xai"));
                break;
            case 3:
                store.Save(settings.WithProvider("nvidia"));
                break;
            case 4:
                store.Save(settings.WithProvider(null));
                break;
        }
    }

    private static void SaveApiKeyFromPrompt(GlobalSettingsStore store, GlobalSettings settings, ProviderKind provider)
    {
        var providerName = ProviderParsing.NormalizeProvider(provider);
        var apiKey = ReadSecret($"API key for {providerName} (empty clears): ");
        store.Save(settings.WithApiKey(provider, apiKey));
    }

    private static void ManageApiKeysFromMenu(GlobalSettingsStore store, GlobalSettings settings)
    {
        var choice = RunMenu(
            "api keys",
            [
                ("anthropic", MaskSecret(settings.ApiKeys?.Anthropic)),
                ("openai", MaskSecret(settings.ApiKeys?.OpenAi)),
                ("xai", MaskSecret(settings.ApiKeys?.Xai)),
                ("nvidia", MaskSecret(settings.ApiKeys?.Nvidia))
            ],
            "Use ↑/↓ to move, Enter to edit, Esc to go back.",
            allowEscape: true
        );

        var provider = choice switch
        {
            0 => ProviderKind.CodeSharpApi,
            1 => ProviderKind.OpenAi,
            2 => ProviderKind.Xai,
            3 => ProviderKind.Nvidia,
            _ => (ProviderKind?)null
        };

        if (provider is { } selectedProvider)
        {
            Console.Clear();
            SaveApiKeyFromPrompt(store, settings, selectedProvider);
        }
    }

    private static void ClearValueFromMenu(GlobalSettingsStore store, GlobalSettings settings)
    {
        var choice = RunMenu(
            "clear values",
            [
                ("Model", settings.Model ?? "(already empty)"),
                ("Provider", settings.Provider ?? "(already empty)"),
                ("Anthropic key", MaskSecret(settings.ApiKeys?.Anthropic)),
                ("OpenAI key", MaskSecret(settings.ApiKeys?.OpenAi)),
                ("xAI key", MaskSecret(settings.ApiKeys?.Xai)),
                ("NVIDIA key", MaskSecret(settings.ApiKeys?.Nvidia))
            ],
            "Use ↑/↓ to move, Enter to clear, Esc to go back.",
            allowEscape: true
        );

        var updated = choice switch
        {
            0 => settings.WithModel(null),
            1 => settings.WithProvider(null),
            2 => settings.WithApiKey(ProviderKind.CodeSharpApi, null),
            3 => settings.WithApiKey(ProviderKind.OpenAi, null),
            4 => settings.WithApiKey(ProviderKind.Xai, null),
            5 => settings.WithApiKey(ProviderKind.Nvidia, null),
            _ => null
        };

        if (updated is null)
        {
            Console.WriteLine(ConsoleUi.Warning("Unknown value."));
            return;
        }

        store.Save(updated);
    }

    private static int CountConfiguredKeys(GlobalSettings settings)
    {
        var count = 0;
        if (!string.IsNullOrWhiteSpace(settings.ApiKeys?.Anthropic)) count++;
        if (!string.IsNullOrWhiteSpace(settings.ApiKeys?.OpenAi)) count++;
        if (!string.IsNullOrWhiteSpace(settings.ApiKeys?.Xai)) count++;
        if (!string.IsNullOrWhiteSpace(settings.ApiKeys?.Nvidia)) count++;
        return count;
    }

    private static int RunMenu(
        string title,
        IReadOnlyList<(string Label, string Value)> items,
        string footer,
        bool allowEscape = false
    )
    {
        var selected = 0;

        while (true)
        {
            Console.Clear();
            Console.WriteLine(ConsoleUi.Panel(
                title,
                items.Select((item, index) => (
                    index == selected ? $"› {item.Label}" : $"  {item.Label}",
                    item.Value
                )),
                footer
            ));

            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    selected = selected == 0 ? items.Count - 1 : selected - 1;
                    break;
                case ConsoleKey.DownArrow:
                    selected = selected == items.Count - 1 ? 0 : selected + 1;
                    break;
                case ConsoleKey.Enter:
                    return selected;
                case ConsoleKey.Escape when allowEscape:
                    return -1;
            }
        }
    }

    private static void PauseWithMessage(string title, string body, string? footer = null)
    {
        Console.Clear();
        Console.WriteLine(ConsoleUi.MessageBlock(title, body, footer));
        Console.ReadKey(intercept: true);
    }

    private static string? ReadSecret(string prompt)
    {
        try
        {
            Console.Write(prompt);
            var secret = new StringBuilder();

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

internal static class ProviderParsing
{
    public static bool TryParseProvider(string value, out ProviderKind provider)
    {
        switch (NormalizeProvider(value))
        {
            case "anthropic":
                provider = ProviderKind.CodeSharpApi;
                return true;
            case "openai":
                provider = ProviderKind.OpenAi;
                return true;
            case "xai":
                provider = ProviderKind.Xai;
                return true;
            case "nvidia":
                provider = ProviderKind.Nvidia;
                return true;
            default:
                provider = default;
                return false;
        }
    }

    public static string NormalizeProvider(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "anthropic" => "anthropic",
            "openai" => "openai",
            "xai" => "xai",
            "nvidia" => "nvidia",
            _ => value.Trim().ToLowerInvariant()
        };

    public static string NormalizeProvider(ProviderKind provider) => provider switch
    {
        ProviderKind.CodeSharpApi => "anthropic",
        ProviderKind.OpenAi => "openai",
        ProviderKind.Xai => "xai",
        ProviderKind.Nvidia => "nvidia",
        _ => provider.ToString().ToLowerInvariant()
    };
}
