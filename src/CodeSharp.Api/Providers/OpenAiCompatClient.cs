using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using CodeSharp.Core;

namespace CodeSharp.Api.Providers;

public class OpenAiCompatClient : IProvider
{
    private static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DefaultMaxBackoff = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan NvidiaInitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan NvidiaMaxBackoff = TimeSpan.FromSeconds(30);

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatConfig _config;
    private readonly int _maxRetries;
    private readonly TimeSpan _initialBackoff;
    private readonly TimeSpan _maxBackoff;

    public string ProviderName => _config.ProviderName;

    public OpenAiCompatClient(OpenAiCompatConfig config)
    {
        _config = config;
        _httpClient = new HttpClient();
        (_maxRetries, _initialBackoff, _maxBackoff) = config.ProviderName == "Nvidia"
            ? (10, NvidiaInitialBackoff, NvidiaMaxBackoff)
            : (4, DefaultInitialBackoff, DefaultMaxBackoff);

        var apiKey = config.ApiKey ?? Environment.GetEnvironmentVariable(config.ApiKeyEnvVar);
        if (!string.IsNullOrEmpty(apiKey))
        {
            if (config.ProviderName == "Nvidia")
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
            else
            {
                _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");
            }
        }
    }
    
    public static OpenAiCompatClient FromEnv(OpenAiCompatConfig config)
    {
        var apiKey = Environment.GetEnvironmentVariable(config.ApiKeyEnvVar);
        return new OpenAiCompatClient(config with { ApiKey = apiKey });
    }
    
    public async Task<MessageResponse> SendMessageAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        var openAiRequest = ConvertToOpenAiRequest(request);
        var json = JsonSerializer.Serialize(openAiRequest);

        using var response = await SendWithRetryAsync(
            () => CreateChatCompletionRequest(json),
            cancellationToken
        );

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ApiError(FormatApiError(response.StatusCode, errorContent));
        }

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseOpenAiResponse(responseJson);
    }
    
    public async IAsyncEnumerable<StreamEvent> StreamMessageAsync(MessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var openAiRequest = ConvertToOpenAiRequest(request, stream: true);

        var json = JsonSerializer.Serialize(openAiRequest);
        using var response = await SendWithRetryAsync(
            () => CreateChatCompletionRequest(json),
            cancellationToken,
            HttpCompletionOption.ResponseHeadersRead
        );
        
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new ApiError(FormatApiError(response.StatusCode, errorContent));
        }
        
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var messageStopped = false;
        var activeToolCallIndexes = new HashSet<int>();
        var toolCallStates = new Dictionary<int, ToolCallStreamState>();
        
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrEmpty(line))
                continue;
            
            if (!line.StartsWith("data: "))
                continue;
            
            var data = line[6..];
            if (data == "[DONE]")
            {
                if (!messageStopped)
                {
                    messageStopped = true;
                    yield return new StreamEvent.MessageStop();
                }
                yield break;
            }
            
            foreach (var streamEvent in ParseOpenAiStreamEvents(data, activeToolCallIndexes, toolCallStates))
            {
                if (streamEvent is StreamEvent.MessageStop)
                {
                    messageStopped = true;
                }

                yield return streamEvent;
            }
        }
    }
    
    private object ConvertToOpenAiRequest(MessageRequest request, bool stream = false)
    {
        var messages = new List<object>();

        if (!string.IsNullOrEmpty(request.SystemPrompt))
        {
            messages.Add(new { role = "system", content = request.SystemPrompt });
        }

        foreach (var msg in request.Messages)
        {
            messages.AddRange(TranslateMessage(msg));
        }

        var tools = request.Tools?.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = t.InputSchema
            }
        });
        var toolChoice = request.Tools is not null ? "auto" : null;

        // Newer OpenAI models require max_completion_tokens instead of max_tokens
        if (_config.ProviderName == "OpenAi")
        {
            return new
            {
                model = request.Model,
                messages,
                max_completion_tokens = request.MaxTokens,
                stream,
                tools,
                tool_choice = toolChoice
            };
        }

        return new
        {
            model = request.Model,
            messages,
            max_tokens = request.MaxTokens,
            stream,
            tools,
            tool_choice = toolChoice
        };
    }
    
    private MessageResponse ParseOpenAiResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        
        var id = root.GetProperty("id").GetString() ?? string.Empty;
        var model = root.GetProperty("model").GetString() ?? string.Empty;
        var choices = root.GetProperty("choices").EnumerateArray().First();
        var message = choices.GetProperty("message");
        var content = message.GetProperty("content").GetString() ?? string.Empty;
        
        var usage = root.TryGetProperty("usage", out var usageElement)
            ? new TokenUsageResponse(
                usageElement.GetProperty("prompt_tokens").GetInt64(),
                usageElement.GetProperty("completion_tokens").GetInt64()
            )
            : new TokenUsageResponse(0, 0);
        
        return new MessageResponse(
            id,
            model,
            "assistant",
            [new OutputContentBlock.TextBlock(content)],
            new TokenUsage(usage.InputTokens, usage.OutputTokens)
        );
    }
    
    private static IEnumerable<object> TranslateMessage(InputMessage message)
    {
        if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
        {
            var text = new StringBuilder();
            var toolCalls = new List<object>();

            foreach (var block in message.Content)
            {
                switch (block)
                {
                    case InputContentBlock.TextBlock textBlock:
                        text.Append(textBlock.Text);
                        break;
                    case InputContentBlock.ToolUse toolUse:
                        toolCalls.Add(new
                        {
                            id = toolUse.Id,
                            type = "function",
                            function = new
                            {
                                name = toolUse.Name,
                                arguments = toolUse.Input
                            }
                        });
                        break;
                }
            }

            if (text.Length == 0 && toolCalls.Count == 0)
            {
                yield break;
            }

            yield return new
            {
                role = "assistant",
                content = text.Length > 0 ? text.ToString() : null,
                tool_calls = toolCalls.Count > 0 ? toolCalls : null
            };
            yield break;
        }

        foreach (var block in message.Content)
        {
            switch (block)
            {
                case InputContentBlock.TextBlock textBlock:
                    yield return new
                    {
                        role = "user",
                        content = textBlock.Text
                    };
                    break;
                case InputContentBlock.ToolResultBlock toolResult:
                    yield return new
                    {
                        role = "tool",
                        tool_call_id = toolResult.ToolUseId,
                        content = toolResult.Content
                    };
                    break;
            }
        }
    }

    private IReadOnlyList<StreamEvent> ParseOpenAiStreamEvents(
        string data,
        HashSet<int> activeToolCallIndexes,
        Dictionary<int, ToolCallStreamState> toolCallStates
    )
    {
        var events = new List<StreamEvent>();

        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            string? stopReason = null;
            var shouldStop = false;

            var choices = root.GetProperty("choices").EnumerateArray();
            foreach (var choice in choices)
            {
                if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
                {
                    if (delta.TryGetProperty("content", out var contentElement))
                    {
                        var text = contentElement.GetString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(text))
                        {
                            events.Add(new StreamEvent.ContentBlockDelta(0, new ContentBlockDeltaContent.TextDelta(text)));
                        }
                    }

                    if (delta.TryGetProperty("tool_calls", out var toolCalls))
                    {
                        foreach (var toolCall in toolCalls.EnumerateArray())
                        {
                            var index = toolCall.TryGetProperty("index", out var toolIndexElement)
                                ? toolIndexElement.GetInt32()
                                : 0;
                            var id = toolCall.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                            var function = toolCall.TryGetProperty("function", out var functionElement)
                                ? functionElement
                                : default;
                            var name = function.ValueKind == JsonValueKind.Object &&
                                       function.TryGetProperty("name", out var nameElement)
                                ? nameElement.GetString()
                                : null;
                            var args = function.ValueKind == JsonValueKind.Object &&
                                       function.TryGetProperty("arguments", out var argsElement)
                                ? argsElement.GetString()
                                : null;

                            if (!toolCallStates.TryGetValue(index, out var state))
                            {
                                state = new ToolCallStreamState();
                                toolCallStates[index] = state;
                            }

                            var learnedId = !string.IsNullOrEmpty(id) && string.IsNullOrEmpty(state.Id);
                            var learnedName = !string.IsNullOrEmpty(name) && string.IsNullOrEmpty(state.Name);

                            if (!string.IsNullOrEmpty(id))
                            {
                                state.Id = id;
                            }

                            if (!string.IsNullOrEmpty(name))
                            {
                                state.Name = name;
                            }

                            if (!state.Started || learnedId || learnedName)
                            {
                                events.Add(new StreamEvent.ContentBlockStart(index, "tool_use", state.Id, state.Name));
                                state.Started = true;
                            }

                            activeToolCallIndexes.Add(index);

                            if (!string.IsNullOrEmpty(args))
                            {
                                var deltaArgs = MergeToolArguments(state, args);
                                if (!string.IsNullOrEmpty(deltaArgs))
                                {
                                    events.Add(new StreamEvent.ContentBlockDelta(index, new ContentBlockDeltaContent.InputJsonDelta(deltaArgs)));
                                }
                            }
                        }
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var finishReasonElement) && finishReasonElement.ValueKind == JsonValueKind.String)
                {
                    var finishReason = finishReasonElement.GetString();
                    stopReason = NormalizeStopReason(finishReason);
                    if (string.Equals(finishReason, "tool_calls", StringComparison.Ordinal))
                    {
                        foreach (var toolIndex in activeToolCallIndexes.OrderBy(static value => value))
                        {
                            events.Add(new StreamEvent.ContentBlockStop(toolIndex));
                            toolCallStates.Remove(toolIndex);
                        }
                        activeToolCallIndexes.Clear();
                    }
                    shouldStop = true;
                }
            }
            
            if (root.TryGetProperty("usage", out var usageElement))
            {
                events.Add(new StreamEvent.MessageDelta(
                    new TokenUsageResponse(
                        usageElement.GetProperty("prompt_tokens").GetInt64(),
                        usageElement.GetProperty("completion_tokens").GetInt64()
                    ),
                    stopReason
                ));
            }

            if (shouldStop)
            {
                events.Add(new StreamEvent.MessageStop());
            }
        }
        catch
        {
        }
        
        return events;
    }

    private static string MergeToolArguments(ToolCallStreamState state, string incoming)
    {
        if (string.IsNullOrEmpty(incoming))
        {
            return string.Empty;
        }

        state.Arguments += incoming;
        return incoming;
    }

    private HttpRequestMessage CreateChatCompletionRequest(string json) => new(HttpMethod.Post, $"{_config.BaseUrl}/chat/completions")
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead
    )
    {
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var request = requestFactory();
            try
            {
                var response = await _httpClient.SendAsync(request, completionOption, cancellationToken);
                if (!ShouldRetry(response.StatusCode) || attempt >= _maxRetries)
                {
                    return response;
                }

                var delay = GetRetryDelay(response, attempt);
                response.Dispose();
                await Task.Delay(delay, cancellationToken);
            }
            catch (HttpRequestException) when (attempt < _maxRetries)
            {
                await Task.Delay(GetRetryDelay(null, attempt), cancellationToken);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < _maxRetries)
            {
                await Task.Delay(GetRetryDelay(null, attempt), cancellationToken);
            }
        }
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage? response, int attempt)
    {
        if (response?.Headers.RetryAfter?.Delta is { } retryAfter && retryAfter > TimeSpan.Zero)
        {
            return retryAfter <= _maxBackoff ? retryAfter : _maxBackoff;
        }

        var multiplier = Math.Pow(2, attempt);
        var backoff = TimeSpan.FromMilliseconds(_initialBackoff.TotalMilliseconds * multiplier);
        return backoff <= _maxBackoff ? backoff : _maxBackoff;
    }

    private static bool ShouldRetry(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.TooManyRequests ||
        statusCode == HttpStatusCode.RequestTimeout ||
        (int)statusCode >= 500;

    private static string FormatApiError(HttpStatusCode statusCode, string body)
    {
        if (statusCode == HttpStatusCode.TooManyRequests)
        {
            return "api rate limit exceeded (429): please wait a moment and try again";
        }

        return $"API request failed with status {statusCode}: {body}";
    }

    private static string? NormalizeStopReason(string? finishReason)
    {
        return finishReason switch
        {
            null => null,
            "stop" => "end_turn",
            "tool_calls" => "tool_use",
            "length" => "max_tokens",
            "content_filter" => "content_filter",
            _ => finishReason
        };
    }
}

internal sealed class ToolCallStreamState
{
    public string? Id { get; set; }

    public string? Name { get; set; }

    public string Arguments { get; set; } = string.Empty;

    public bool Started { get; set; }
}

public record OpenAiCompatConfig(
    string ProviderName,
    string BaseUrl,
    string ApiKeyEnvVar,
    string? ApiKey = null
)
{
    public static OpenAiCompatConfig OpenAi() => new("OpenAi", "https://api.openai.com/v1", "OPENAI_API_KEY");
    public static OpenAiCompatConfig Xai() => new("Xai", "https://api.x.ai/v1", "XAI_API_KEY");
    public static OpenAiCompatConfig Nvidia() => new("Nvidia", "https://integrate.api.nvidia.com/v1", "NVIDIA_API_KEY");
}
