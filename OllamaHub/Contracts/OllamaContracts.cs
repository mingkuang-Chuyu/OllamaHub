using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaHub.Contracts;

public sealed class OllamaTagListResponse
{
    [JsonPropertyName("models")]
    public required IReadOnlyList<OllamaModelDescriptor> Models { get; init; }
}

public sealed class OllamaModelDescriptor
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("modified_at")]
    public required string ModifiedAt { get; init; }

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("digest")]
    public required string Digest { get; init; }

    [JsonPropertyName("capabilities")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? Capabilities { get; init; }

    [JsonPropertyName("context_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength { get; init; }

    [JsonPropertyName("max_context_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxContextLength { get; init; }

    [JsonPropertyName("max_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxInputTokens { get; init; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("model_info")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, object>? ModelInfo { get; init; }

    [JsonPropertyName("details")]
    public required OllamaModelDetails Details { get; init; }
}

public sealed class OllamaModelDetails
{
    [JsonPropertyName("parent_model")]
    public string ParentModel { get; init; } = string.Empty;

    [JsonPropertyName("format")]
    public string Format { get; init; } = "hub";

    [JsonPropertyName("family")]
    public string Family { get; init; } = "claude";

    [JsonPropertyName("families")]
    public IReadOnlyList<string> Families { get; init; } = [];

    [JsonPropertyName("parameter_size")]
    public string ParameterSize { get; init; } = "unknown";

    [JsonPropertyName("quantization_level")]
    public string QuantizationLevel { get; init; } = "unknown";
}

public sealed class OllamaShowRequest
{
    [JsonPropertyName("model")]
    public string? Model { get; init; }
}

public sealed class OllamaShowResponse
{
    [JsonPropertyName("license")]
    public string License { get; init; } = "proxied";

    [JsonPropertyName("modelfile")]
    public required string Modelfile { get; init; }

    [JsonPropertyName("parameters")]
    public required string Parameters { get; init; }

    [JsonPropertyName("template")]
    public string Template { get; init; } = "{{ .Prompt }}";

    [JsonPropertyName("details")]
    public required OllamaModelDetails Details { get; init; }

    [JsonPropertyName("capabilities")]
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    [JsonPropertyName("context_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContextLength { get; init; }

    [JsonPropertyName("max_context_length")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxContextLength { get; init; }

    [JsonPropertyName("max_input_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxInputTokens { get; init; }

    [JsonPropertyName("max_output_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxOutputTokens { get; init; }

    [JsonPropertyName("model_info")]
    public required IReadOnlyDictionary<string, object> ModelInfo { get; init; }
}

public sealed class OllamaChatRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<OllamaChatMessage> Messages { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;

    [JsonPropertyName("options")]
    public OllamaChatOptions? Options { get; init; }
}

public sealed class OllamaChatMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<OllamaToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolName { get; init; }

    [JsonPropertyName("tool_call_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolCallId { get; init; }
}

public sealed class OllamaToolCall
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public required OllamaToolCallFunction Function { get; init; }
}

public sealed class OllamaToolCallFunction
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("arguments")]
    public JsonNode? Arguments { get; init; }
}

public sealed class OllamaChatOptions
{
    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("num_predict")]
    public int? NumPredict { get; init; }
}

public sealed class OllamaChatChunkResponse
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("created_at")]
    public required string CreatedAt { get; init; }

    [JsonPropertyName("message")]
    public required OllamaChatMessage Message { get; init; }

    [JsonPropertyName("done")]
    public bool Done { get; init; }

    [JsonPropertyName("done_reason")]
    public string? DoneReason { get; init; }
}

public sealed class OllamaErrorResponse
{
    [JsonPropertyName("error")]
    public required string Error { get; init; }
}