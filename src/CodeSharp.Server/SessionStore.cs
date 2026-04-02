using System.Runtime.CompilerServices;
using System.Text.Json;
using CodeSharp.Core;

namespace CodeSharp.Server;

public record SessionInfo(
    string Id,
    string Path,
    int MessageCount,
    long ModifiedEpochSecs
);

public record SessionEvent
{
    public sealed record Snapshot(Session Session) : SessionEvent;
    public sealed record Message(ConversationMessage Msg) : SessionEvent;
}

public class SessionStore
{
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly Dictionary<string, List<SessionEvent>> _events = new();
    private readonly string _sessionsDir;
    
    public SessionStore(string? sessionsDir = null)
    {
        _sessionsDir = sessionsDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".codesharp", "sessions");
        Directory.CreateDirectory(_sessionsDir);
    }
    
    public Session CreateSession()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var session = Session.New();
        _sessions[id] = session;
        _events[id] = new List<SessionEvent> { new SessionEvent.Snapshot(session) };
        return session;
    }
    
    public Session? GetSession(string id)
    {
        return _sessions.TryGetValue(id, out var session) ? session : null;
    }
    
    public IReadOnlyList<SessionInfo> ListSessions()
    {
        return _sessions.Select(kvp => new SessionInfo(
            kvp.Key,
            Path.Combine(_sessionsDir, $"{kvp.Key}.json"),
            kvp.Value.Messages.Count,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        )).ToList();
    }
    
    public void AddMessage(string sessionId, ConversationMessage message)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
        {
            session.AddMessage(message);
            if (_events.TryGetValue(sessionId, out var events))
            {
                events.Add(new SessionEvent.Message(message));
            }
        }
    }
    
    public void SaveSession(string id)
    {
        if (_sessions.TryGetValue(id, out var session))
        {
            var path = Path.Combine(_sessionsDir, $"{id}.json");
            File.WriteAllText(path, session.ToJson());
        }
    }
    
    public IAsyncEnumerable<SessionEvent> GetEventStream(string sessionId, CancellationToken cancellationToken = default)
    {
        return GetEventsAsync(sessionId, cancellationToken);
    }
    
    private async IAsyncEnumerable<SessionEvent> GetEventsAsync(
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        if (_events.TryGetValue(sessionId, out var events))
        {
            foreach (var ev in events)
            {
                if (cancellationToken.IsCancellationRequested)
                    yield break;
                yield return ev;
            }
        }
        
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(1000, cancellationToken);
        }
    }
}
