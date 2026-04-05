using System.Runtime.CompilerServices;
using System.Threading.Channels;
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
    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly Dictionary<string, List<SessionEvent>> _events = new();
    private readonly Dictionary<string, List<Channel<SessionEvent>>> _subscribers = new();
    private readonly string _sessionsDir;
    
    public SessionStore(string? sessionsDir = null)
    {
        _sessionsDir = sessionsDir ?? Path.Combine(Directory.GetCurrentDirectory(), ".codesharp", "sessions");
        Directory.CreateDirectory(_sessionsDir);
    }
    
    public (string Id, Session Session) CreateSession()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var session = Session.New();
        lock (_gate)
        {
            _sessions[id] = session;
            _events[id] = new List<SessionEvent> { new SessionEvent.Snapshot(session.Clone()) };
            _subscribers[id] = [];
        }

        return (id, session);
    }
    
    public Session? GetSession(string id)
    {
        lock (_gate)
        {
            return _sessions.TryGetValue(id, out var session) ? session : null;
        }
    }
    
    public IReadOnlyList<SessionInfo> ListSessions()
    {
        lock (_gate)
        {
            return _sessions.Select(kvp => new SessionInfo(
                kvp.Key,
                Path.Combine(_sessionsDir, $"{kvp.Key}.json"),
                kvp.Value.Messages.Count,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            )).ToList();
        }
    }
    
    public void AddMessage(string sessionId, ConversationMessage message)
    {
        List<Channel<SessionEvent>> subscribers;
        SessionEvent sessionEvent;

        lock (_gate)
        {
            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                return;
            }

            session.AddMessage(message);
            sessionEvent = new SessionEvent.Message(message);

            if (_events.TryGetValue(sessionId, out var events))
            {
                events.Add(sessionEvent);
            }

            subscribers = _subscribers.TryGetValue(sessionId, out var channels)
                ? channels.ToList()
                : [];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Writer.TryWrite(sessionEvent);
        }
    }
    
    public void SaveSession(string id)
    {
        string? json = null;
        lock (_gate)
        {
            if (_sessions.TryGetValue(id, out var session))
            {
                json = session.ToJson();
            }
        }

        if (json is null)
        {
            return;
        }

        var path = Path.Combine(_sessionsDir, $"{id}.json");
        File.WriteAllText(path, json);
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
        List<SessionEvent> replay;
        var channel = Channel.CreateUnbounded<SessionEvent>();
        var sessionExists = true;

        lock (_gate)
        {
            if (!_events.TryGetValue(sessionId, out var events))
            {
                replay = [];
                sessionExists = false;
            }
            else
            {
                replay = events.ToList();

                if (!_subscribers.TryGetValue(sessionId, out var subscribers))
                {
                    subscribers = [];
                    _subscribers[sessionId] = subscribers;
                }

                subscribers.Add(channel);
            }
        }

        if (!sessionExists)
        {
            channel.Writer.TryComplete();
            yield break;
        }

        try
        {
            foreach (var ev in replay)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ev;
            }

            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var ev))
                {
                    yield return ev;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_subscribers.TryGetValue(sessionId, out var subscribers))
                {
                    subscribers.Remove(channel);
                }
            }

            channel.Writer.TryComplete();
        }
    }
}
