using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace CodeSharp.Api.Providers;

public interface IProvider
{
    string ProviderName { get; }
    Task<MessageResponse> SendMessageAsync(MessageRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<StreamEvent> StreamMessageAsync(MessageRequest request, CancellationToken cancellationToken = default);
}

public class ApiError : Exception
{
    public ApiError(string message) : base(message) { }
    public ApiError(string message, Exception inner) : base(message, inner) { }
}

public static class HttpClientExtensions
{
    public static async Task<MessageResponse> SendMessageAsync(
        this HttpClient client,
        string baseUrl,
        MessageRequest request,
        CancellationToken cancellationToken = default
    )
    {
        var response = await client.PostAsJsonAsync($"{baseUrl}/messages", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<MessageResponse>(cancellationToken);
        return result ?? throw new ApiError("Failed to deserialize response");
    }
}
