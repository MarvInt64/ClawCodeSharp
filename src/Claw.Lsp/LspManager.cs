namespace Claw.Lsp;

public record LspServerConfig(
    string Id,
    string Command,
    IReadOnlyList<string>? Args = null,
    string? WorkspaceRoot = null
);

public record LspDiagnostic(
    string Uri,
    int Line,
    int Column,
    string Message,
    string Severity,
    string? Source = null,
    string? Code = null
);

public record LspLocation(
    string Uri,
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn
);

public record LspContextEnrichment(
    IReadOnlyList<LspDiagnostic> Diagnostics,
    IReadOnlyList<LspLocation> Definitions,
    IReadOnlyList<LspLocation> References
);

public class LspManager : IAsyncDisposable
{
    private readonly List<LspServer> _servers = new();
    private readonly Dictionary<string, List<LspDiagnostic>> _diagnostics = new();
    
    public LspManager() { }
    
    public async Task AddServerAsync(LspServerConfig config, CancellationToken cancellationToken = default)
    {
        var server = new LspServer(config);
        await server.StartAsync(cancellationToken);
        _servers.Add(server);
    }
    
    public Task<LspContextEnrichment> GetContextEnrichmentAsync(
        string fileUri,
        CancellationToken cancellationToken = default
    )
    {
        var diagnostics = _diagnostics.TryGetValue(fileUri, out var diags)
            ? diags
            : new List<LspDiagnostic>();

        return Task.FromResult(new LspContextEnrichment(
            diagnostics,
            Array.Empty<LspLocation>(),
            Array.Empty<LspLocation>()
        ));
    }
    
    public IReadOnlyList<LspDiagnostic> GetAllDiagnostics()
    {
        return _diagnostics.Values.SelectMany(d => d).ToList();
    }
    
    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }
        _servers.Clear();
    }
}

internal class LspServer : IAsyncDisposable
{
    private readonly LspServerConfig _config;
    private readonly CancellationTokenSource _cts = new();
    
    public LspServer(LspServerConfig config)
    {
        _config = config;
    }
    
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
    }
    
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _cts.Dispose();
        await ValueTask.CompletedTask;
    }
}
