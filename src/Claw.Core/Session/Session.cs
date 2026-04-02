namespace Claw.Core;

public class Session
{
    private readonly List<ConversationMessage> _messages = new();
    private int _version = 1;
    
    public IReadOnlyList<ConversationMessage> Messages => _messages.AsReadOnly();
    public int Count => _messages.Count;
    public int Version => _version;
    
    public Session() { }
    
    private Session(List<ConversationMessage> messages, int version)
    {
        _messages = messages;
        _version = version;
    }
    
    public void AddMessage(ConversationMessage message)
    {
        _messages.Add(message);
        _version++;
    }
    
    public bool RemoveLastUserMessage()
    {
        if (_messages.Count > 0 && _messages[^1].Role == MessageRole.User)
        {
            _messages.RemoveAt(_messages.Count - 1);
            _version++;
            return true;
        }
        return false;
    }

    public void Clear()
    {
        if (_messages.Count == 0)
        {
            return;
        }

        _messages.Clear();
        _version++;
    }

    public void Truncate(int count)
    {
        if (count < 0 || count > _messages.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == _messages.Count)
        {
            return;
        }

        _messages.RemoveRange(count, _messages.Count - count);
        _version++;
    }
    
    public Session Clone() => new(new List<ConversationMessage>(_messages), _version);
    
    public static Session New() => new();
    
    public string ToJson()
    {
        var dto = new SessionDto
        {
            Version = _version,
            Messages = _messages.Select(m => new ConversationMessageDto
            {
                Role = m.Role.ToString(),
                Blocks = m.Blocks.Select(ToContentBlockDto).ToList(),
                Usage = m.Usage is null ? null : new TokenUsageDto
                {
                    InputTokens = m.Usage.InputTokens,
                    OutputTokens = m.Usage.OutputTokens,
                    CacheCreationInputTokens = m.Usage.CacheCreationInputTokens,
                    CacheReadInputTokens = m.Usage.CacheReadInputTokens,
                }
            }).ToList()
        };
        return System.Text.Json.JsonSerializer.Serialize(dto);
    }
    
    public static Session FromJson(string json)
    {
        var dto = System.Text.Json.JsonSerializer.Deserialize<SessionDto>(json);
        if (dto is null)
            throw new InvalidOperationException("Failed to deserialize session");
        
        var messages = dto.Messages.Select(m => new ConversationMessage(
            Enum.Parse<MessageRole>(m.Role),
            m.Blocks.Select(FromContentBlockDto).ToList(),
            m.Usage is null ? null : new TokenUsage(
                m.Usage.InputTokens,
                m.Usage.OutputTokens,
                m.Usage.CacheCreationInputTokens,
                m.Usage.CacheReadInputTokens
            )
        )).ToList();
        
        return new Session(messages, dto.Version);
    }
    
    private static ContentBlockDto ToContentBlockDto(ContentBlock block) => block switch
    {
        ContentBlock.Text t => new ContentBlockDto { Type = "text", Text = t.Content },
        ContentBlock.ToolUse tu => new ContentBlockDto
        {
            Type = "tool_use",
            Id = tu.Id,
            Name = tu.Name,
            Input = tu.Input
        },
        ContentBlock.ToolResult tr => new ContentBlockDto
        {
            Type = "tool_result",
            ToolUseId = tr.ToolUseId,
            ToolName = tr.ToolName,
            Content = tr.Output,
            IsError = tr.IsError
        },
        _ => throw new InvalidOperationException($"Unknown block type: {block.GetType()}")
    };
    
    private static ContentBlock FromContentBlockDto(ContentBlockDto dto) => dto.Type switch
    {
        "text" => new ContentBlock.Text(dto.Text ?? string.Empty),
        "tool_use" => new ContentBlock.ToolUse(
            dto.Id ?? string.Empty,
            dto.Name ?? string.Empty,
            dto.Input ?? string.Empty
        ),
        "tool_result" => new ContentBlock.ToolResult(
            dto.ToolUseId ?? string.Empty,
            dto.ToolName ?? string.Empty,
            dto.Content ?? string.Empty,
            dto.IsError ?? false
        ),
        _ => throw new InvalidOperationException($"Unknown block type: {dto.Type}")
    };
}

internal class SessionDto
{
    public int Version { get; set; }
    public List<ConversationMessageDto> Messages { get; set; } = new();
}

internal class ConversationMessageDto
{
    public string Role { get; set; } = string.Empty;
    public List<ContentBlockDto> Blocks { get; set; } = new();
    public TokenUsageDto? Usage { get; set; }
}

internal class ContentBlockDto
{
    public string Type { get; set; } = string.Empty;
    public string? Text { get; set; }
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Input { get; set; }
    public string? ToolUseId { get; set; }
    public string? ToolName { get; set; }
    public string? Content { get; set; }
    public bool? IsError { get; set; }
}

internal class TokenUsageDto
{
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheCreationInputTokens { get; set; }
    public long CacheReadInputTokens { get; set; }
}
