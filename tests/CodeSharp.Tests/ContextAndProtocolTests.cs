using System.Text.Json;
using CodeSharp.Api;
using CodeSharp.Api.Providers;
using CodeSharp.Cli;
using CodeSharp.Commands;
using CodeSharp.Core;
using CodeSharp.Server;
using CodeSharp.Tools;
using Xunit;

namespace CodeSharp.Tests;

public class ContextAndProtocolTests
{
    [Fact]
    public void CompactForContext_DoesNotStartTailWithOrphanedToolResult()
    {
        var messages = new List<ConversationMessage>
        {
            ConversationMessage.UserText("Inspect the repo."),
            ConversationMessage.AssistantText("I will inspect the repo."),
            ConversationMessage.UserText("Search for TODOs and FIXME."),
            ConversationMessage.AssistantWithUsage(
            [
                new ContentBlock.Text("I will run both searches."),
                new ContentBlock.ToolUse("tool-1", "grep_search", """{"pattern":"TODO","path":"."}"""),
                new ContentBlock.ToolUse("tool-2", "grep_search", """{"pattern":"FIXME","path":"."}""")
            ]),
            ConversationMessage.ToolResult(
                "tool-1",
                "grep_search",
                """{"pattern":"TODO","totalMatches":1,"matches":["Program.cs:42 TODO"]}""",
                false
            ),
            ConversationMessage.ToolResult(
                "tool-2",
                "grep_search",
                """{"pattern":"FIXME","totalMatches":0,"matches":[]}""",
                false
            ),
            ConversationMessage.AssistantText("I found one TODO and no FIXME."),
            ConversationMessage.UserText("Anything else?"),
            ConversationMessage.AssistantText("No further issues surfaced.")
        };

        var compacted = SessionCompactor.CompactForContext(messages, keepTailMessages: 4);

        Assert.Equal(MessageRole.User, compacted[0].Role);
        Assert.StartsWith("[Earlier conversation compacted", GetText(compacted[0]));
        Assert.Equal(MessageRole.Assistant, compacted[1].Role);
        Assert.Equal(2, compacted[1].Blocks.OfType<ContentBlock.ToolUse>().Count());
        Assert.Equal(MessageRole.Tool, compacted[2].Role);
        Assert.Equal(MessageRole.Tool, compacted[3].Role);
    }

    [Fact]
    public void BuildTransportRequest_KeepsToolResponsesAttachedToAssistantToolCalls()
    {
        var client = new OpenAiCompatClient(OpenAiCompatConfig.OpenAi() with { ApiKey = "test-key" });
        var request = new MessageRequest(
            "gpt-5-mini",
            [
                new InputMessage("user",
                [
                    new InputContentBlock.TextBlock("Search for TODOs.")
                ]),
                new InputMessage("assistant",
                [
                    new InputContentBlock.TextBlock("I will search the repo."),
                    new InputContentBlock.ToolUse("tool-1", "grep_search", """{"pattern":"TODO","path":"."}""")
                ]),
                new InputMessage("tool",
                [
                    new InputContentBlock.ToolResultBlock("tool-1", """{"totalMatches":1}""")
                ])
            ],
            Tools:
            [
                new ToolDefinition("grep_search", "Search the repo", new { type = "object" })
            ],
            SystemPrompt: "You are CodeSharp."
        );

        var payload = client.BuildTransportRequest(request);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var messages = document.RootElement.GetProperty("messages").EnumerateArray().ToList();

        Assert.Equal("system", messages[0].GetProperty("role").GetString());
        Assert.Equal("user", messages[1].GetProperty("role").GetString());
        Assert.Equal("assistant", messages[2].GetProperty("role").GetString());
        Assert.True(messages[2].TryGetProperty("tool_calls", out var toolCalls));
        Assert.Equal(1, toolCalls.GetArrayLength());
        Assert.Equal("tool", messages[3].GetProperty("role").GetString());
        Assert.Equal("tool-1", messages[3].GetProperty("tool_call_id").GetString());
    }

    [Fact]
    public void CreateSession_ReturnsStableStoreId()
    {
        var store = new SessionStore(Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}"));

        var (id, session) = store.CreateSession();

        Assert.NotNull(session);
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.Same(session, store.GetSession(id));
    }

    [Fact]
    public async Task GetEventStream_EmitsLiveMessagesAfterSubscription()
    {
        var store = new SessionStore(Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}"));
        var (id, _) = store.CreateSession();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var enumerator = store.GetEventStream(id, cts.Token).GetAsyncEnumerator(cts.Token);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.IsType<SessionEvent.Snapshot>(enumerator.Current);

        store.AddMessage(id, ConversationMessage.UserText("hello"));

        Assert.True(await enumerator.MoveNextAsync());
        var messageEvent = Assert.IsType<SessionEvent.Message>(enumerator.Current);
        Assert.Equal(MessageRole.User, messageEvent.Msg.Role);
        Assert.Equal("hello", GetText(messageEvent.Msg));
    }

    [Fact]
    public async Task FindSymbol_FindsDeclarationsAcrossMultipleLanguages()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "native"));
        Directory.CreateDirectory(Path.Combine(workspace, "web"));

        await File.WriteAllTextAsync(Path.Combine(workspace, "api.py"), """
class Greeter:
    def say_hello(self, name):
        return f"Hello {name}"
""");

        await File.WriteAllTextAsync(Path.Combine(workspace, "web", "client.ts"), """
export class ApiClient {
  fetchUser() {
    return null;
  }
}
""");

        await File.WriteAllTextAsync(Path.Combine(workspace, "native", "render.cpp"), """
int RenderFrame() {
    return 0;
}
""");

        await File.WriteAllTextAsync(Path.Combine(workspace, "index.html"), """
<body>
  <div id="app-root" class="shell layout"></div>
</body>
""");

        var executor = new ToolExecutor(new GlobalToolRegistry(), workspace);

        var typeSearch = await executor.ExecuteAsync("find_symbol", """{"symbol":"ApiClient","match_type":"exact"}""");
        using var typeDoc = JsonDocument.Parse(typeSearch.Output);
        Assert.Equal(1, typeDoc.RootElement.GetProperty("totalMatches").GetInt32());
        var typeMatch = typeDoc.RootElement.GetProperty("matches")[0];
        Assert.Equal("typescript", typeMatch.GetProperty("language").GetString());
        Assert.Equal("class", typeMatch.GetProperty("kind").GetString());

        var htmlSearch = await executor.ExecuteAsync("find_symbol", """{"symbol":"app-root","match_type":"exact"}""");
        using var htmlDoc = JsonDocument.Parse(htmlSearch.Output);
        Assert.Equal(1, htmlDoc.RootElement.GetProperty("totalMatches").GetInt32());
        Assert.Equal("html_id", htmlDoc.RootElement.GetProperty("matches")[0].GetProperty("kind").GetString());
    }

    [Fact]
    public async Task FindReferences_FindsUsagesAcrossWorkspace()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "web"));

        await File.WriteAllTextAsync(Path.Combine(workspace, "web", "client.ts"), """
export class ApiClient {}
const api = new ApiClient();
function loadClient(client: ApiClient) {
  return client;
}
""");

        var executor = new ToolExecutor(new GlobalToolRegistry(), workspace);
        var result = await executor.ExecuteAsync("find_references", """{"symbol":"ApiClient","include_declarations":true}""");

        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(3, document.RootElement.GetProperty("totalReferences").GetInt32());
        Assert.Equal(1, document.RootElement.GetProperty("definitions").GetArrayLength());
    }

    [Fact]
    public void SlashCommand_Parse_SupportsSymbolsAndReferences()
    {
        var symbols = SlashCommand.Parse("/symbols ApiClient");
        Assert.NotNull(symbols);
        Assert.Equal(SlashCommandKind.Symbols, symbols!.Kind);
        Assert.Equal("ApiClient", symbols.Args);

        var refs = SlashCommand.Parse("/refs ApiClient");
        Assert.NotNull(refs);
        Assert.Equal(SlashCommandKind.References, refs!.Kind);
        Assert.Equal("ApiClient", refs.Args);
    }

    [Fact]
    public void AutoVerifyPlanner_PrefersDotNetBuildForCSharpChanges()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        File.WriteAllText(Path.Combine(workspace, "CodeSharp.sln"), string.Empty);
        var changedFile = Path.Combine(workspace, "src", "Program.cs");
        File.WriteAllText(changedFile, "class Program {}");

        var plan = AutoVerifyPlanner.TryCreate(workspace, [changedFile]);

        Assert.NotNull(plan);
        Assert.Equal("dotnet build", plan!.Command);
        Assert.Equal(".NET build", plan.Strategy);
    }

    [Fact]
    public void AutoVerifyPlanner_UsesPackageManagerTypecheckForTypeScriptChanges()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        File.WriteAllText(Path.Combine(workspace, "pnpm-lock.yaml"), string.Empty);
        File.WriteAllText(Path.Combine(workspace, "package.json"), """
{
  "name": "demo",
  "scripts": {
    "typecheck": "tsc --noEmit",
    "build": "vite build"
  }
}
""");

        var changedFile = Path.Combine(workspace, "src", "app.ts");
        File.WriteAllText(changedFile, "export const x = 1;");

        var plan = AutoVerifyPlanner.TryCreate(workspace, [changedFile]);

        Assert.NotNull(plan);
        Assert.Equal("pnpm run typecheck", plan!.Command);
        Assert.Equal("Node typecheck", plan.Strategy);
    }

    [Fact]
    public void AutoVerifyPlanner_UsesPyCompileForChangedPythonFiles()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "pkg"));
        File.WriteAllText(Path.Combine(workspace, "pyproject.toml"), """
[project]
name = "demo"
version = "0.1.0"
""");

        var changedFile = Path.Combine(workspace, "pkg", "app.py");
        File.WriteAllText(changedFile, "print('hello')");

        var plan = AutoVerifyPlanner.TryCreate(workspace, [changedFile]);

        Assert.NotNull(plan);
        Assert.StartsWith("python -m py_compile ", plan!.Command, StringComparison.Ordinal);
        Assert.Contains("app.py", plan.Command, StringComparison.Ordinal);
        Assert.Equal("Python syntax check", plan.Strategy);
    }

    [Fact]
    public void SynchronizeModelSelection_AlsoSynchronizesProvider()
    {
        var settings = new GlobalSettings();

        var updated = ProviderAccessWorkflow.SynchronizeModelSelection(settings, "kimi2.5");

        Assert.Equal("moonshotai/kimi-k2.5", updated.Model);
        Assert.Equal("nvidia", updated.Provider);
    }

    [Fact]
    public void ResolveProviderKind_FollowsModelUnlessExplicitlyOverridden()
    {
        Assert.Equal(ProviderKind.Nvidia, ProviderAccessWorkflow.ResolveProviderKind("moonshotai/kimi-k2.5"));
        Assert.Equal(ProviderKind.OpenAi, ProviderAccessWorkflow.ResolveProviderKind("moonshotai/kimi-k2.5", ProviderKind.OpenAi));
    }

    private static string GetText(ConversationMessage message) =>
        string.Join(
            "\n",
            message.Blocks
                .OfType<ContentBlock.Text>()
                .Select(static block => block.Content)
        );
}
