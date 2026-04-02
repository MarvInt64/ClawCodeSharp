using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Claw.Api.Providers;

public class ClawApiClient : IProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly string? _oauthToken;
    
    public string ProviderName => "ClawApi";
    
    public ClawApiClient(string? apiKey = null, string? oauthToken = null, string? baseUrl = null)
    {
        _apiKey = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        _oauthToken = oauthToken;
        _baseUrl = baseUrl ?? Environment.GetEnvironmentVariable("CLAW_API_BASE_URL") ?? "https://api.anthropic.com";
        _httpClient = new HttpClient();
    }
    
    public static ClawApiClient FromEnv()
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        var oauthToken = Environment.GetEnvironmentVariable("CLAW_OAUTH_TOKEN");
        var baseUrl = Environment.GetEnvironmentVariable("CLAW_API_BASE_URL");
        
        return new ClawApiClient(apiKey, oauthToken, baseUrl);
    }
    
    public async Task<MessageResponse> SendMessageAsync(MessageRequest request, CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateHttpRequest(request);
        var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<MessageResponse>(json)
            ?? throw new ApiError("Failed to deserialize response");
    }
    
    public async IAsyncEnumerable<StreamEvent> StreamMessageAsync(MessageRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streamRequest = request with { };
        using var httpRequest = CreateHttpRequest(streamRequest, stream: true);
        
        using var response = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        
        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line))
                continue;
                
            if (!line.StartsWith("data: "))
                continue;
            
            var json = line[6..];
            if (json == "[DONE]")
                yield break;
            
            var streamEvent = JsonSerializer.Deserialize<StreamEvent>(json);
            if (streamEvent is not null)
                yield return streamEvent;
        }
    }
    
    private HttpRequestMessage CreateHttpRequest(MessageRequest request, bool stream = false)
    {
        var url = stream ? $"{_baseUrl}/v1/messages?stream=true" : $"{_baseUrl}/v1/messages";
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        
        if (!string.IsNullOrEmpty(_apiKey))
            httpRequest.Headers.Add("x-api-key", _apiKey);
        else if (!string.IsNullOrEmpty(_oauthToken))
            httpRequest.Headers.Add("Authorization", $"Bearer {_oauthToken}");
        
        httpRequest.Headers.Add("anthropic-version", "2023-06-01");
        
        var json = JsonSerializer.Serialize(request);
        httpRequest.Content = new StringContent(json, Encoding.UTF8, "application/json");
        
        return httpRequest;
    }
}
