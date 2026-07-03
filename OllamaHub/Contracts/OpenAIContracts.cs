using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaHub.Contracts;

public sealed class OpenAIChatCompletionsRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<OpenAIChatMessage> Messages { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; init; }

    [JsonPropertyName("max_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("tools")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OpenAIToolDefinition>? Tools { get; init; }

    [JsonPropertyName("tool_choice")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? ToolChoice { get; init; }

    [JsonExtensionData]
    public Dictionary<string, object?> Extra { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OpenAIChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OpenAIToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }
}

public sealed class OpenAIToolDefinition
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required OpenAIToolFunctionDefinition Function { get; init; }
}

public sealed class OpenAIToolFunctionDefinition
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; init; }

    [JsonPropertyName("parameters")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Parameters { get; init; }
}

public sealed class OpenAIToolCall
{
    [JsonPropertyName("index")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Index { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required OpenAIToolCallFunction Function { get; init; }
}

public sealed class OpenAIToolCallFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public string Arguments { get; init; } = "{}";
}

public sealed class OpenAIChatCompletionsResponse
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("choices")]
    public required IReadOnlyList<OpenAIChatChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIUsage? Usage { get; init; }
}

public sealed class OpenAIChatChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("message")]
    public required OpenAIChatMessage Message { get; init; }

    [JsonPropertyName("finish_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }
}

public sealed class OpenAIChatCompletionChunk
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("object")]
    public string Object { get; init; } = "chat.completion.chunk";

    [JsonPropertyName("created")]
    public long Created { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("choices")]
    public required IReadOnlyList<OpenAIChatChunkChoice> Choices { get; init; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIUsage? Usage { get; init; }
}

public sealed class OpenAIChatChunkChoice
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("delta")]
    public required OpenAIChatDelta Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FinishReason { get; init; }
}

public sealed class OpenAIChatDelta
{
    [JsonPropertyName("role")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OpenAIToolCall>? ToolCalls { get; init; }
}

public sealed class OpenAIUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; init; }
}
