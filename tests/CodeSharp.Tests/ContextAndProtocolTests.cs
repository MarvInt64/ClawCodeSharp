using System.Text.Json;
using CodeSharp.Api;
using CodeSharp.Api.Providers;
using CodeSharp.Cli;
using CodeSharp.Commands;
using CodeSharp.Core;
using CodeSharp.Server;
using CodeSharp.Tools;
using Xunit;
using ApiToolDefinition = CodeSharp.Api.ToolDefinition;

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
                new ApiToolDefinition("grep_search", "Search the repo", new { type = "object" })
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
    public async Task TaskCreate_Explore_ReturnsWorkspaceSummaryAndSuggestedFiles()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        Directory.CreateDirectory(Path.Combine(workspace, "src"));
        Directory.CreateDirectory(Path.Combine(workspace, "tests"));

        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "Program.cs"), """
using Demo;

Console.WriteLine("hello");
""");

        await File.WriteAllTextAsync(Path.Combine(workspace, "src", "ApiClient.ts"), """
export class ApiClient {
  fetchUser() {
    return null;
  }
}
""");

        await File.WriteAllTextAsync(Path.Combine(workspace, "README.md"), """
# Demo

This repository contains an api client and entry point.
""");

        var executor = new ToolExecutor(new GlobalToolRegistry(), workspace);
        var result = await executor.ExecuteAsync(
            "TaskCreate",
            """{"description":"understand the project layout","prompt":"analyze api client entry points","subagent_type":"Explore"}""");

        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal("Explore", document.RootElement.GetProperty("subagentType").GetString());
        Assert.Equal("completed", document.RootElement.GetProperty("status").GetString());
        Assert.True(document.RootElement.GetProperty("totalFiles").GetInt32() >= 3);
        Assert.Contains("Workspace scan covered", document.RootElement.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.True(document.RootElement.GetProperty("languages").GetArrayLength() > 0);
        Assert.True(document.RootElement.GetProperty("suggestedFiles").GetArrayLength() > 0);
    }

    [Fact]
    public async Task TaskList_And_TaskGet_ReturnStoredExploreTasks()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "Program.cs"), "class Program {}");

        var executor = new ToolExecutor(new GlobalToolRegistry(), workspace);
        var created = await executor.ExecuteAsync(
            "TaskCreate",
            """{"description":"map the workspace","prompt":"look for entry points","subagent_type":"Explore"}""");

        using var createdDoc = JsonDocument.Parse(created.Output);
        var taskId = createdDoc.RootElement.GetProperty("taskId").GetString();
        Assert.False(string.IsNullOrWhiteSpace(taskId));

        var list = await executor.ExecuteAsync("TaskList", """{}""");
        using var listDoc = JsonDocument.Parse(list.Output);
        Assert.Equal(1, listDoc.RootElement.GetProperty("totalTasks").GetInt32());
        Assert.Equal(taskId, listDoc.RootElement.GetProperty("tasks")[0].GetProperty("taskId").GetString());

        var fetched = await executor.ExecuteAsync("TaskGet", $$"""{"task_id":"{{taskId}}"}""");
        using var fetchedDoc = JsonDocument.Parse(fetched.Output);
        Assert.Equal(taskId, fetchedDoc.RootElement.GetProperty("taskId").GetString());
        Assert.Equal("Explore", fetchedDoc.RootElement.GetProperty("subagentType").GetString());
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

        var plan = SlashCommand.Parse("/plan approve");
        Assert.NotNull(plan);
        Assert.Equal(SlashCommandKind.Plan, plan!.Kind);
        Assert.Equal("approve", plan.Args);
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

    [Fact]
    public async Task RunTurnAsync_RunsAutomaticVerificationOnceAtTurnEnd()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "Demo.sln"), string.Empty);
        var originalCwd = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(workspace);

            var api = new FakeApiClient(
                new AssistantEvent[][]
                {
                    new AssistantEvent[]
                    {
                        new AssistantEvent.TextDelta("I will write the file."),
                        new AssistantEvent.ToolUse("tool-1", "write_file", """{"path":"Program.cs","content":"class Program {}"}"""),
                        new AssistantEvent.MessageStop()
                    },
                    new AssistantEvent[]
                    {
                        new AssistantEvent.TextDelta("The edit is done."),
                        new AssistantEvent.MessageStop()
                    },
                    new AssistantEvent[]
                    {
                        new AssistantEvent.TextDelta("Verification passed."),
                        new AssistantEvent.MessageStop()
                    }
                });

            var tools = new FakeToolExecutor();
            var runtime = new ConversationRuntime(
                Session.New(),
                api,
                tools,
                new PermissionPolicy(
                    PermissionMode.DangerFullAccess,
                    new Dictionary<string, PermissionMode>
                    {
                        ["write_file"] = PermissionMode.WorkspaceWrite,
                        ["bash"] = PermissionMode.DangerFullAccess
                    }),
                ["You are CodeSharp."],
                PermissionMode.DangerFullAccess,
                AutoVerifyMode.On
            );

            var summary = await runtime.RunTurnAsync("make the change");

            Assert.Equal(3, api.Requests.Count);
            Assert.Equal(["write_file", "bash"], tools.ExecutedTools);
            Assert.Contains(runtime.Session.Messages, message =>
                message.Role == MessageRole.User &&
                GetText(message).Contains("[Runtime verification note]", StringComparison.Ordinal));
            Assert.Equal("Verification passed.", ExtractLatestAssistantText(summary));
        }
        finally
        {
            Directory.SetCurrentDirectory(originalCwd);
        }
    }

    [Fact]
    public async Task RunTurnAsync_InPlanningMode_BlocksMutatingToolsAndAddsPlanningPrompt()
    {
        var api = new FakeApiClient(
            new[]
            {
                new AssistantEvent[]
                {
                    new AssistantEvent.TextDelta("I will edit the file."),
                    new AssistantEvent.ToolUse("tool-1", "write_file", """{"path":"Program.cs","content":"class Program {}"}"""),
                    new AssistantEvent.MessageStop()
                },
                new AssistantEvent[]
                {
                    new AssistantEvent.TextDelta("Goal\n- Outline the change\n\nWaiting for approval."),
                    new AssistantEvent.MessageStop()
                }
            });

        var tools = new FakeToolExecutor();
        var runtime = new ConversationRuntime(
            Session.New(),
            api,
            tools,
            new PermissionPolicy(
                PermissionMode.DangerFullAccess,
                new Dictionary<string, PermissionMode>
                {
                    ["write_file"] = PermissionMode.WorkspaceWrite
                }),
            ["You are CodeSharp."],
            PermissionMode.DangerFullAccess,
            AutoVerifyMode.On
        )
        {
            Mode = AgentExecutionMode.Planning,
            PlanningDepth = "deep"
        };

        var summary = await runtime.RunTurnAsync("plan this change");

        Assert.Equal(2, api.Requests.Count);
        Assert.Empty(tools.ExecutedTools);
        Assert.Contains("Planning Mode", api.Requests[0].SystemPrompt.Last(), StringComparison.Ordinal);
        Assert.Contains("TaskCreate/TaskGet/TaskList", api.Requests[0].SystemPrompt.Last(), StringComparison.Ordinal);
        Assert.Contains("deeper planning pass", api.Requests[0].SystemPrompt.Last(), StringComparison.Ordinal);
        Assert.Single(summary.ToolResults);
        Assert.Contains(
            "Planning mode blocks `write_file`",
            summary.ToolResults[0].Blocks.OfType<ContentBlock.ToolResult>().Single().Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunTurnAsync_PreservesNewlinesInAssistantDraftStream()
    {
        var api = new FakeApiClient(
            new[]
            {
                new AssistantEvent[]
                {
                    new AssistantEvent.TextDelta("# Title\n"),
                    new AssistantEvent.TextDelta("- first item\n"),
                    new AssistantEvent.TextDelta("- second item"),
                    new AssistantEvent.MessageStop()
                }
            });

        var runtime = new ConversationRuntime(
            Session.New(),
            api,
            new FakeToolExecutor(),
            new PermissionPolicy(PermissionMode.DangerFullAccess, new Dictionary<string, PermissionMode>()),
            ["You are CodeSharp."],
            PermissionMode.DangerFullAccess,
            AutoVerifyMode.Off
        );

        var activities = new List<RuntimeActivity>();
        await runtime.RunTurnAsync("respond in markdown", activitySink: activities.Add);

        var finalDraft = activities.OfType<RuntimeActivity.AssistantDraft>().Last();
        Assert.Contains('\n', finalDraft.Text);
        Assert.Contains("- first item", finalDraft.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatActivityLines_RendersAssistantDraftMarkdownDuringStreaming()
    {
        var lines = Program.FormatActivityLines(
            new ActivityLine(
                "assistant-draft",
                "assistant_draft",
                "# Title\n\n- first item\n- second item",
                ActivityLineStatus.Info,
                DetailLines: Program.RenderAssistantDraftPreviewLines("# Title\n\n- first item\n- second item")));

        Assert.True(lines.Count >= 3);
        Assert.Contains(lines, line => line.Contains("drafting response", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(lines, line => line.Contains("Title", StringComparison.Ordinal));
        Assert.Contains(lines, line => line.Contains("first item", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EditFile_ReturnsDiffCountsAndHeadTailPreview()
    {
        var workspace = Path.Combine(Path.GetTempPath(), $"codesharp-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspace);
        var filePath = Path.Combine(workspace, "Example.cs");
        var original = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"line {i}"));
        await File.WriteAllTextAsync(filePath, original);

        var replacement = string.Join("\n", Enumerable.Range(1, 24).Select(i => $"replacement {i}"));
        var executor = new ToolExecutor(new GlobalToolRegistry(), workspace);
        var result = await executor.ExecuteAsync(
            "edit_file",
            JsonSerializer.Serialize(new
            {
                path = "Example.cs",
                old_string = original,
                new_string = replacement
            }));

        using var document = JsonDocument.Parse(result.Output);
        Assert.Equal(24, document.RootElement.GetProperty("linesAdded").GetInt32());
        Assert.Equal(30, document.RootElement.GetProperty("linesRemoved").GetInt32());
        Assert.True(document.RootElement.GetProperty("previewTruncated").GetBoolean());

        var preview = document.RootElement.GetProperty("preview").EnumerateArray().Select(x => x.GetString()).ToList();
        Assert.Contains("… 34 more diff lines …", preview);
        Assert.Contains("+ replacement 24", preview);
    }

    [Fact]
    public void DescribeToolFinish_IncludesDiffCountsForFileEdits()
    {
        var description = Program.DescribeToolFinish(
            "edit_file",
            """{"path":"src/App.cs","linesAdded":13,"linesRemoved":9,"preview":["@@ -1,1 +1,1 @@"]}""",
            "editing src/App.cs",
            false
        );

        Assert.Equal("edited src/App.cs (+13 -9)", description);
    }

    private static string GetText(ConversationMessage message) =>
        string.Join(
            "\n",
            message.Blocks
                .OfType<ContentBlock.Text>()
                .Select(static block => block.Content)
        );

    private static string ExtractLatestAssistantText(TurnSummary summary) =>
        summary.AssistantMessages
            .Select(GetText)
            .Last();

    private sealed class FakeApiClient(IEnumerable<IReadOnlyList<AssistantEvent>> responses) : IApiClient
    {
        private readonly Queue<IReadOnlyList<AssistantEvent>> _responses = new(responses);

        public List<ApiRequest> Requests { get; } = [];

        public Task<IReadOnlyList<AssistantEvent>> StreamAsync(
            ApiRequest request,
            Action<AssistantEvent>? eventSink = null,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);
            var response = _responses.Dequeue();
            if (eventSink is not null)
            {
                foreach (var assistantEvent in response)
                {
                    eventSink(assistantEvent);
                }
            }

            return Task.FromResult(response);
        }
    }

    private sealed class FakeToolExecutor : IToolExecutor
    {
        public List<string> ExecutedTools { get; } = [];

        public Task<ToolResult> ExecuteAsync(string toolName, string input, CancellationToken cancellationToken = default)
        {
            ExecutedTools.Add(toolName);
            return toolName switch
            {
                "write_file" => Task.FromResult(new ToolResult("""{"path":"Program.cs","linesWritten":1,"preview":["+class Program {}"],"previewTruncated":false}""")),
                "bash" => Task.FromResult(new ToolResult("""{"stdout":"Build succeeded.","stderr":"","exitCode":0}""")),
                _ => throw new InvalidOperationException($"Unexpected tool: {toolName}")
            };
        }
    }
}
