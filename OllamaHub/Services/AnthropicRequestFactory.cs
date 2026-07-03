using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaHub.Configuration;
using OllamaHub.Contracts;

namespace OllamaHub.Services;

public interface IAnthropicRequestFactory
{
    AnthropicMessagesRequest Create(ResolvedModelConfig model, OllamaChatRequest request);

    AnthropicMessagesRequest Create(ResolvedModelConfig model, JsonObject request);

    AnthropicMessagesRequest Create(ResolvedModelConfig model, OpenAIChatCompletionsRequest request);
}

public sealed class AnthropicRequestFactory : IAnthropicRequestFactory
{
    private static readonly HashSet<string> SupportedAnthropicExtraFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "metadata",
        "service_tier",
        "stop_sequences",
        "thinking",
        "mcp_servers",
        "container",
        "context_management"
    };

    public AnthropicMessagesRequest Create(ResolvedModelConfig model, OllamaChatRequest request)
    {
        var extra = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in model.Extra)
        {
            extra[pair.Key] = pair.Value;
        }

        var systemMessages = new List<string>();
        var messages = request.Messages.SelectMany(message => ConvertOllamaMessage(message, systemMessages)).ToList();

        return new AnthropicMessagesRequest
        {
            Model = model.AnthropicModel,
            System = systemMessages.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, systemMessages) : null,
            Messages = messages,
            Stream = request.Stream,
            Temperature = request.Options?.Temperature ?? model.Temperature,
            TopP = request.Options?.TopP ?? model.TopP,
            MaxTokens = request.Options?.NumPredict ?? model.MaxTokens,
            Extra = extra
        };
    }

    public AnthropicMessagesRequest Create(ResolvedModelConfig model, OpenAIChatCompletionsRequest request)
    {
        var requestJson = JsonSerializer.SerializeToNode(request)?.AsObject() ?? new JsonObject();
        return Create(model, requestJson);
    }

    public AnthropicMessagesRequest Create(ResolvedModelConfig model, JsonObject request)
    {
        var systemMessages = new List<string>();
        var extra = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in model.Extra)
        {
            extra[pair.Key] = pair.Value;
        }

        foreach (var pair in request)
        {
            if (SupportedAnthropicExtraFields.Contains(pair.Key))
            {
                extra[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var messages = (request["messages"] as JsonArray)?.SelectMany(message => ConvertOpenAiMessage(message, systemMessages)).ToList()
            ?? [];

        return new AnthropicMessagesRequest
        {
            Model = model.AnthropicModel,
            System = systemMessages.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, systemMessages) : null,
            Messages = messages,
            Stream = TryGetBoolean(request, "stream") ?? false,
            Temperature = TryGetDouble(request, "temperature") ?? model.Temperature,
            TopP = TryGetDouble(request, "top_p") ?? model.TopP,
            MaxTokens = TryGetInt32(request, "max_tokens") ?? model.MaxTokens,
            Tools = ExtractTools(request["tools"]),
            ToolChoice = MapToolChoice(request["tool_choice"]?.DeepClone()),
            Extra = extra
        };
    }

    private static JsonNode? MapToolChoice(JsonNode? toolChoice)
    {
        if (toolChoice is null)
        {
            return null;
        }

        if (toolChoice is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            return stringValue.ToLowerInvariant() switch
            {
                "auto" => JsonNode.Parse("""{"type":"auto"}"""),
                "required" => JsonNode.Parse("""{"type":"any"}"""),
                "none" => null,
                _ => null
            };
        }

        if (toolChoice is JsonObject obj)
        {
            var type = TryGetString(obj["type"]);
            if (string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                var functionName = TryGetString(obj["function"]?["name"]);
                if (!string.IsNullOrWhiteSpace(functionName))
                {
                    return new JsonObject
                    {
                        ["type"] = "tool",
                        ["name"] = functionName
                    };
                }
            }

            if (string.Equals(type, "auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "any", StringComparison.OrdinalIgnoreCase)
                || string.Equals(type, "tool", StringComparison.OrdinalIgnoreCase))
            {
                return obj.DeepClone();
            }
        }

        return null;
    }

    private static IEnumerable<AnthropicMessage> ConvertOllamaMessage(OllamaChatMessage message, List<string> systemMessages)
    {
        if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                systemMessages.Add(message.Content);
            }

            return [];
        }

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content =
                    [
                        new AnthropicContentBlock
                        {
                            Type = "tool_result",
                            ToolUseId = message.ToolCallId ?? message.ToolName ?? "tool-call",
                            Content = message.Content ?? string.Empty,
                            IsError = false
                        }
                    ]
                }
            ];
        }

        return [CreateStandardMessage(message.Role, ExtractOllamaContentBlocks(message.Content), message.ToolCalls?.Select(ToAnthropicToolUse).ToArray())];
    }

    private static IEnumerable<AnthropicMessage> ConvertOpenAiMessage(JsonNode? messageNode, List<string> systemMessages)
    {
        if (messageNode is not JsonObject message)
        {
            return [];
        }

        var role = TryGetString(message["role"]);
        var content = message["content"]?.DeepClone();

        if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
        {
            var text = ExtractText(content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                systemMessages.Add(text);
            }

            return [];
        }

        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content =
                    [
                        new AnthropicContentBlock
                        {
                            Type = "tool_result",
                            ToolUseId = TryGetString(message["tool_call_id"]) ?? TryGetString(message["name"]) ?? "tool-call",
                            Content = ExtractText(content) ?? string.Empty,
                            IsError = false
                        }
                    ]
                }
            ];
        }

        var toolCalls = ExtractToolCalls(message["tool_calls"]);

        return [CreateStandardMessage(role ?? "user", ExtractContentBlocks(content), toolCalls)];
    }

    private static AnthropicMessage CreateStandardMessage(string role, IReadOnlyList<AnthropicContentBlock>? contentBlocks, IReadOnlyList<AnthropicContentBlock>? toolCalls)
    {
        var content = new List<AnthropicContentBlock>();
        if (contentBlocks is not null)
        {
            content.AddRange(contentBlocks);
        }

        if (toolCalls is not null)
        {
            content.AddRange(toolCalls);
        }

        return new AnthropicMessage
        {
            Role = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
            Content = content.Count > 0
                ? content
                :
                [
                    new AnthropicContentBlock
                    {
                        Type = "text",
                        Text = string.Empty
                    }
                ]
        };
    }

    private static IReadOnlyList<AnthropicContentBlock> ExtractContentBlocks(JsonNode? content)
    {
        return content switch
        {
            null => [],
            JsonValue value => value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
                ? [new AnthropicContentBlock { Type = "text", Text = text }]
                : [],
            JsonArray array => array.SelectMany(ConvertContentPart).ToArray(),
            _ => ExtractText(content) is { Length: > 0 } fallback
                ? [new AnthropicContentBlock { Type = "text", Text = fallback }]
                : []
        };
    }

    private static IReadOnlyList<AnthropicContentBlock> ExtractOllamaContentBlocks(string? content)
    {
        return string.IsNullOrWhiteSpace(content)
            ? []
            :
            [
                new AnthropicContentBlock
                {
                    Type = "text",
                    Text = content
                }
            ];
    }

    private static AnthropicContentBlock ToAnthropicToolUse(OllamaToolCall toolCall) =>
        new()
        {
            Type = "tool_use",
            Id = toolCall.Id,
            Name = toolCall.Function.Name,
            Input = toolCall.Function.Arguments
        };

    private static JsonNode? ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments);
        }
        catch
        {
            return JsonValue.Create(arguments);
        }
    }

    private static string? ExtractText(JsonNode? content)
    {
        return content switch
        {
            null => null,
            JsonValue value => value.TryGetValue<string>(out var text) ? text : content.ToJsonString(),
            JsonArray array => string.Join(string.Empty, array.SelectMany(static item => ExtractContentParts(item))),
            _ => content.ToJsonString()
        };
    }

    private static IEnumerable<string> ExtractContentParts(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "text" && obj["text"] is JsonNode textNode)
            {
                var text = textNode.GetValue<string>();
                return string.IsNullOrEmpty(text) ? [] : [text];
            }

            return [];
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            return string.IsNullOrEmpty(stringValue) ? [] : [stringValue];
        }

        return [];
    }

    private static AnthropicToolDefinition[]? ExtractTools(JsonNode? toolsNode)
    {
        if (toolsNode is not JsonArray toolsArray)
        {
            return null;
        }

        var tools = toolsArray
            .Select(ConvertToolDefinition)
            .OfType<AnthropicToolDefinition>()
            .ToArray();

        return tools.Length == 0 ? null : tools;
    }

    private static AnthropicToolDefinition? ConvertToolDefinition(JsonNode? toolNode)
    {
        if (toolNode is not JsonObject toolObject || toolObject["function"] is not JsonObject functionObject)
        {
            return null;
        }

        var name = TryGetString(functionObject["name"]);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new AnthropicToolDefinition
        {
            Name = name,
            Description = TryGetString(functionObject["description"]),
            InputSchema = functionObject["parameters"]?.DeepClone()
        };
    }

    private static AnthropicContentBlock[]? ExtractToolCalls(JsonNode? toolCallsNode)
    {
        if (toolCallsNode is not JsonArray toolCallsArray)
        {
            return null;
        }

        var toolCalls = toolCallsArray
            .Select(ConvertToolCall)
            .OfType<AnthropicContentBlock>()
            .ToArray();

        return toolCalls.Length == 0 ? null : toolCalls;
    }

    private static AnthropicContentBlock? ConvertToolCall(JsonNode? toolCallNode)
    {
        if (toolCallNode is not JsonObject toolCallObject || toolCallObject["function"] is not JsonObject functionObject)
        {
            return null;
        }

        var id = TryGetString(toolCallObject["id"]);
        var name = TryGetString(functionObject["name"]);
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        return new AnthropicContentBlock
        {
            Type = "tool_use",
            Id = id,
            Name = name,
            Input = ParseArguments(TryGetString(functionObject["arguments"]) ?? "{}")
        };
    }

    private static bool? TryGetBoolean(JsonObject jsonObject, string propertyName)
    {
        return jsonObject[propertyName] is JsonValue value && value.TryGetValue<bool>(out var result)
            ? result
            : null;
    }

    private static double? TryGetDouble(JsonObject jsonObject, string propertyName)
    {
        return jsonObject[propertyName] is JsonValue value && value.TryGetValue<double>(out var result)
            ? result
            : null;
    }

    private static int? TryGetInt32(JsonObject jsonObject, string propertyName)
    {
        return jsonObject[propertyName] is JsonValue value && value.TryGetValue<int>(out var result)
            ? result
            : null;
    }

    private static string? TryGetString(JsonNode? node)
    {
        return node is JsonValue value && value.TryGetValue<string>(out var result)
            ? result
            : null;
    }

    private static IEnumerable<AnthropicContentBlock> ConvertContentPart(JsonNode? node)
    {
        if (node is not JsonObject obj)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text))
            {
                return [new AnthropicContentBlock { Type = "text", Text = text }];
            }

            return [];
        }

        var type = TryGetString(obj["type"]);
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
        {
            var text = TryGetString(obj["text"]);
            return string.IsNullOrWhiteSpace(text)
                ? []
                : [new AnthropicContentBlock { Type = "text", Text = text }];
        }

        if (string.Equals(type, "image_url", StringComparison.OrdinalIgnoreCase) && obj["image_url"] is JsonObject imageUrl)
        {
            var url = TryGetString(imageUrl["url"]);
            if (string.IsNullOrWhiteSpace(url))
            {
                return [];
            }

            if (TryParseDataUrl(url, out var mediaType, out var data))
            {
                return
                [
                    new AnthropicContentBlock
                    {
                        Type = "image",
                        Source = new AnthropicContentSource
                        {
                            Type = "base64",
                            MediaType = mediaType,
                            Data = data
                        }
                    }
                ];
            }

            return
            [
                new AnthropicContentBlock
                {
                    Type = "image",
                    Source = new AnthropicContentSource
                    {
                        Type = "url",
                        Url = url
                    }
                }
            ];
        }

        return [];
    }

    private static bool TryParseDataUrl(string url, out string mediaType, out string data)
    {
        mediaType = string.Empty;
        data = string.Empty;

        if (!url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var commaIndex = url.IndexOf(',');
        if (commaIndex <= 5)
        {
            return false;
        }

        var metadata = url[5..commaIndex];
        if (!metadata.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        mediaType = metadata[..^7];
        data = url[(commaIndex + 1)..];
        return !string.IsNullOrWhiteSpace(mediaType) && !string.IsNullOrWhiteSpace(data);
    }
}
