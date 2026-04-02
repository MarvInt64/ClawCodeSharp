namespace CodeSharp.Core;

public record UsageTrackerSnapshot(IReadOnlyList<TokenUsage> TurnUsages, int Turns);

public class UsageTracker
{
    private readonly List<TokenUsage> _turnUsages = new();
    private int _turns;
    
    public UsageTracker() { }
    
    private UsageTracker(List<TokenUsage> turnUsages, int turns)
    {
        _turnUsages = turnUsages;
        _turns = turns;
    }
    
    public static UsageTracker FromSession(Session session)
    {
        var turnUsages = new List<TokenUsage>();
        var turns = 0;
        
        foreach (var message in session.Messages)
        {
            if (message.Role == MessageRole.Assistant && message.Usage is not null)
            {
                turnUsages.Add(message.Usage);
                turns++;
            }
        }
        
        return new UsageTracker(turnUsages, turns);
    }
    
    public void Record(TokenUsage usage)
    {
        _turnUsages.Add(usage);
        _turns++;
    }

    public UsageTrackerSnapshot Snapshot() => new(_turnUsages.ToList(), _turns);

    public void Restore(UsageTrackerSnapshot snapshot)
    {
        _turnUsages.Clear();
        _turnUsages.AddRange(snapshot.TurnUsages);
        _turns = snapshot.Turns;
    }
    
    public TokenUsage CumulativeUsage()
    {
        var inputTokens = 0L;
        var outputTokens = 0L;
        var cacheCreationTokens = 0L;
        var cacheReadTokens = 0L;
        
        foreach (var usage in _turnUsages)
        {
            inputTokens += usage.InputTokens;
            outputTokens += usage.OutputTokens;
            cacheCreationTokens += usage.CacheCreationInputTokens;
            cacheReadTokens += usage.CacheReadInputTokens;
        }
        
        return new TokenUsage(inputTokens, outputTokens, cacheCreationTokens, cacheReadTokens);
    }
    
    public TokenUsage CurrentTurnUsage() => _turnUsages.Count > 0 ? _turnUsages[^1] : new TokenUsage(0, 0);
    
    public int Turns() => _turns;
}
