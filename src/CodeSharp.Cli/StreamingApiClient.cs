using System.Text;
using CodeSharp.Api;
using CodeSharp.Core;
using CodeSharp.Tools;
using ToolDefinition = CodeSharp.Tools.ToolDefinition;

namespace CodeSharp.Cli;

public class StreamingApiClient : IApiClient
{
    private readonly ProviderClient _client;
    private readonly string _model;
    private readonly IReadOnlyList<Api.ToolDefinition> _tools;

    public StreamingApiClient(ProviderClient client, string model, IReadOnlyList<Api.ToolDefinition> tools)
    {
        _client = client;
        _model = model;
        _tools = tools;
    }
    
    public async Task<IReadOnlyList<AssistantEvent>> StreamAsync(
        ApiRequest request,
        Action<AssistantEvent>? eventSink = null,
        CancellationToken cancellationToken = default
    )
    {
        var inputMessages = request.Messages.Select(ConvertMessage).ToList();
        
        var messageRequest = new MessageRequest(
            _model,
            inputMessages,
            _tools.Count > 0 ? _tools : null,
            string.Join("\n\n", request.SystemPrompt),
            ModelAliases.MaxTokensForModel(_model)
        );
        
        var events = new List<AssistantEvent>();
        var contentBuffers = new Dictionary<int, StringBuilder>();
        var toolCalls = new Dictionary<int, (string Id, string Name, StringBuilder Input)>();
        
        await foreach (var streamEvent in _client.StreamMessageAsync(messageRequest, cancellationToken))
        {
            switch (streamEvent)
            {
                case StreamEvent.MessageStart ms:
                {
                    var assistantEvent = new AssistantEvent.Usage(new TokenUsage(
                        ms.Usage.InputTokens,
                        ms.Usage.OutputTokens,
                        ms.Usage.CacheCreationInputTokens ?? 0,
                        ms.Usage.CacheReadInputTokens ?? 0
                    ));
                    events.Add(assistantEvent);
                    eventSink?.Invoke(assistantEvent);
                    break;
                }
                    
                case StreamEvent.ContentBlockStart cbs:
                    if (cbs.Type == "tool_use")
                    {
                        if (toolCalls.TryGetValue(cbs.Index, out var existing))
                        {
                            toolCalls[cbs.Index] = (
                                string.IsNullOrEmpty(cbs.Id) ? existing.Id : cbs.Id,
                                string.IsNullOrEmpty(cbs.Name) ? existing.Name : cbs.Name,
                                existing.Input
                            );
                        }
                        else
                        {
                            toolCalls[cbs.Index] = (cbs.Id ?? string.Empty, cbs.Name ?? string.Empty, new StringBuilder());
                        }
                    }
                    else
                    {
                        contentBuffers[cbs.Index] = new StringBuilder();
                    }
                    break;
                    
                case StreamEvent.ContentBlockDelta cbd:
                    switch (cbd.Delta)
                    {
                        case ContentBlockDeltaContent.TextDelta td:
                        {
                            var assistantEvent = new AssistantEvent.TextDelta(td.Text);
                            events.Add(assistantEvent);
                            eventSink?.Invoke(assistantEvent);
                            break;
                        }
                        case ContentBlockDeltaContent.InputJsonDelta ijd:
                            if (!toolCalls.TryGetValue(cbd.Index, out var tc))
                            {
                                tc = (string.Empty, string.Empty, new StringBuilder());
                                toolCalls[cbd.Index] = tc;
                            }
                            tc.Input.Append(ijd.PartialJson);
                            break;
                    }
                    break;
                    
                case StreamEvent.ContentBlockStop cbs:
                    if (toolCalls.TryGetValue(cbs.Index, out var tc2))
                    {
                        var assistantEvent = new AssistantEvent.ToolUse(tc2.Id, tc2.Name, tc2.Input.ToString());
                        events.Add(assistantEvent);
                        eventSink?.Invoke(assistantEvent);
                        toolCalls.Remove(cbs.Index);
                    }
                    break;
                    
                case StreamEvent.MessageDelta md:
                {
                    var assistantEvent = new AssistantEvent.Usage(new TokenUsage(
                        md.Usage.InputTokens,
                        md.Usage.OutputTokens,
                        md.Usage.CacheCreationInputTokens ?? 0,
                        md.Usage.CacheReadInputTokens ?? 0
                    ));
                    events.Add(assistantEvent);
                    eventSink?.Invoke(assistantEvent);
                    break;
                }
                    
                case StreamEvent.MessageStop:
                {
                    var assistantEvent = new AssistantEvent.MessageStop();
                    events.Add(assistantEvent);
                    eventSink?.Invoke(assistantEvent);
                    break;
                }
            }
        }
        
        return events;
    }
    
    private InputMessage ConvertMessage(ConversationMessage msg)
    {
        var blocks = msg.Blocks.Select(b => b switch
        {
            ContentBlock.Text t => (InputContentBlock)new InputContentBlock.TextBlock(t.Content),
            ContentBlock.ToolUse tu => new InputContentBlock.ToolUse(tu.Id, tu.Name, tu.Input),
            ContentBlock.ToolResult tr => new InputContentBlock.ToolResultBlock(tr.ToolUseId, tr.Output, tr.IsError),
            _ => throw new InvalidOperationException($"Unknown block type: {b.GetType()}")
        }).ToList();

        return new InputMessage(msg.Role.ToString().ToLowerInvariant(), blocks);
    }
}
