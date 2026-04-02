using System.Net;
using System.Text.Json;
using Claw.Core;

namespace Claw.Server;

public class HttpServer
{
    private readonly HttpListener _listener = new();
    private readonly SessionStore _sessionStore;
    private readonly int _port;
    private CancellationTokenSource? _cts;
    
    public HttpServer(int port = 3000, string? sessionsDir = null)
    {
        _port = port;
        _sessionStore = new SessionStore(sessionsDir);
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }
    
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        
        Console.WriteLine($"Server started on http://localhost:{_port}/");
        
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = HandleRequestAsync(context, _cts.Token);
            }
            catch (HttpListenerException) when (_cts.Token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
        }
    }
    
    public void Stop()
    {
        _cts?.Cancel();
        _listener.Stop();
    }
    
    private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var response = context.Response;
        
        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            
            switch (path)
            {
                case "/" when request.HttpMethod == "GET":
                    await ServeIndex(response);
                    break;
                    
                case "/sessions" when request.HttpMethod == "GET":
                    await ListSessions(response);
                    break;
                    
                case "/sessions" when request.HttpMethod == "POST":
                    await CreateSession(response);
                    break;
                    
                case var p when p.StartsWith("/sessions/") && request.HttpMethod == "GET":
                    await GetSession(response, p[10..]);
                    break;
                    
                case var p when p.StartsWith("/sessions/") && p.EndsWith("/events") && request.HttpMethod == "GET":
                    await StreamEvents(response, p[10..].Replace("/events", ""), cancellationToken);
                    break;
                    
                case var p when p.StartsWith("/sessions/") && p.EndsWith("/message") && request.HttpMethod == "POST":
                    await SendMessage(request, response, p[10..].Replace("/message", ""));
                    break;
                    
                default:
                    response.StatusCode = 404;
                    await WriteJson(response, new { error = "Not found" });
                    break;
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 500;
            await WriteJson(response, new { error = ex.Message });
        }
        finally
        {
            response.Close();
        }
    }
    
    private static async Task ServeIndex(HttpListenerResponse response)
    {
        var index = new
        {
            name = "Claw Code Server",
            version = "0.1.0",
            endpoints = new[]
            {
                "GET /",
                "GET /sessions",
                "POST /sessions",
                "GET /sessions/{id}",
                "GET /sessions/{id}/events",
                "POST /sessions/{id}/message"
            }
        };
        
        await WriteJson(response, index);
    }
    
    private async Task ListSessions(HttpListenerResponse response)
    {
        var sessions = _sessionStore.ListSessions();
        await WriteJson(response, sessions);
    }
    
    private async Task CreateSession(HttpListenerResponse response)
    {
        var session = _sessionStore.CreateSession();
        await WriteJson(response, new { id = session.GetHashCode().ToString("x"), created = true });
    }
    
    private async Task GetSession(HttpListenerResponse response, string id)
    {
        var session = _sessionStore.GetSession(id);
        if (session is null)
        {
            response.StatusCode = 404;
            await WriteJson(response, new { error = "Session not found" });
            return;
        }
        
        await WriteJson(response, new
        {
            id,
            messages = session.Messages.Count,
            version = session.Version
        });
    }
    
    private async Task StreamEvents(HttpListenerResponse response, string id, CancellationToken cancellationToken)
    {
        response.ContentType = "text/event-stream";
        response.Headers.Add("Cache-Control", "no-cache");
        response.Headers.Add("Connection", "keep-alive");
        
        await foreach (var ev in _sessionStore.GetEventStream(id, cancellationToken))
        {
            var data = JsonSerializer.Serialize(ev);
            var sse = $"data: {data}\n\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(sse);
            await response.OutputStream.WriteAsync(bytes, cancellationToken);
            await response.OutputStream.FlushAsync(cancellationToken);
        }
    }
    
    private async Task SendMessage(HttpListenerRequest request, HttpListenerResponse response, string id)
    {
        using var reader = new StreamReader(request.InputStream);
        var body = await reader.ReadToEndAsync();
        var msgRequest = JsonSerializer.Deserialize<MessageRequest>(body);
        
        if (msgRequest is null)
        {
            response.StatusCode = 400;
            await WriteJson(response, new { error = "Invalid request" });
            return;
        }
        
        var message = new ConversationMessage(
            Enum.Parse<MessageRole>(msgRequest.Role),
            msgRequest.Blocks.Select(b => new ContentBlock.Text(b)).ToList()
        );
        
        _sessionStore.AddMessage(id, message);
        _sessionStore.SaveSession(id);
        
        await WriteJson(response, new { sent = true });
    }
    
    private static async Task WriteJson(HttpListenerResponse response, object data)
    {
        response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        await response.OutputStream.WriteAsync(bytes);
    }
}

internal class MessageRequest
{
    public string Role { get; set; } = string.Empty;
    public List<string> Blocks { get; set; } = new();
}
