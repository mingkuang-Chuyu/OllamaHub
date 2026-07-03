using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Serialization;

namespace OllamaHub.Services;

public interface IAnthropicResponseMapper
{
    OllamaChatChunkResponse MapMessageResponse(ResolvedModelConfig model, AnthropicMessagesResponse response);

    OpenAIChatCompletionsResponse MapOpenAiResponse(ResolvedModelConfig model, AnthropicMessagesResponse response);

    Task WriteStreamAsync(ResolvedModelConfig model, Stream anthropicStream, Stream output, CancellationToken cancellationToken);

    Task WriteOpenAiStreamAsync(ResolvedModelConfig model, Stream anthropicStream, Stream output, CancellationToken cancellationToken);
}

public sealed class AnthropicResponseMapper : IAnthropicResponseMapper
{
    public OllamaChatChunkResponse MapMessageResponse(ResolvedModelConfig model, AnthropicMessagesResponse response)
    {
        var text = string.Concat(response.Content?
            .Where(content => string.Equals(content.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(content => content.Text) ?? []);

        return CreateChunk(model.OllamaModelName, text, true, response.StopReason);
    }

    public OpenAIChatCompletionsResponse MapOpenAiResponse(ResolvedModelConfig model, AnthropicMessagesResponse response)
    {
        var message = CreateOpenAiMessage(response.Content);

        return new OpenAIChatCompletionsResponse
        {
            Id = CreateCompletionId(),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = model.OllamaModelName,
            Choices =
            [
                new OpenAIChatChoice
                {
                    Index = 0,
                    Message = message,
                    FinishReason = MapFinishReason(response.StopReason, message.ToolCalls)
                }
            ],
            Usage = MapUsage(response.Usage)
        };
    }

    public async Task WriteStreamAsync(ResolvedModelConfig model, Stream anthropicStream, Stream output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(anthropicStream);
        await using var writer = new StreamWriter(output, leaveOpen: true);
        string? eventName = null;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                eventName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var eventType = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : eventName;

            if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("text", out var textElement))
            {
                await WriteChunkAsync(writer, CreateChunk(model.OllamaModelName, textElement.GetString() ?? string.Empty, false, null), cancellationToken);
                continue;
            }

            if (string.Equals(eventType, "message_delta", StringComparison.OrdinalIgnoreCase))
            {
                var stopReason = root.TryGetProperty("delta", out var deltaNode)
                    && deltaNode.TryGetProperty("stop_reason", out var stopReasonNode)
                    ? stopReasonNode.GetString()
                    : null;

                await WriteChunkAsync(writer, CreateChunk(model.OllamaModelName, string.Empty, true, stopReason), cancellationToken);
            }
        }

        await writer.FlushAsync(cancellationToken);
    }

    public async Task WriteOpenAiStreamAsync(ResolvedModelConfig model, Stream anthropicStream, Stream output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(anthropicStream);
        await using var writer = new StreamWriter(output, leaveOpen: true);
        string? eventName = null;
        var completionId = CreateCompletionId();
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var toolIndexes = new Dictionary<int, int>();
        var usage = new AnthropicUsage();

        await WriteOpenAiSseAsync(writer, new OpenAIChatCompletionChunk
        {
            Id = completionId,
            Created = created,
            Model = model.OllamaModelName,
            Choices =
            [
                new OpenAIChatChunkChoice
                {
                    Index = 0,
                    Delta = new OpenAIChatDelta
                    {
                        Role = "assistant"
                    }
                }
            ]
        }, cancellationToken);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                eventName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var eventType = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : eventName;

            if (string.Equals(eventType, "message_start", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("message", out var messageNode))
            {
                usage = MergeUsage(usage, ExtractUsage(messageNode));
                continue;
            }

            if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("text", out var textElement))
            {
                await WriteOpenAiSseAsync(writer, new OpenAIChatCompletionChunk
                {
                    Id = completionId,
                    Created = created,
                    Model = model.OllamaModelName,
                    Choices =
                    [
                        new OpenAIChatChunkChoice
                        {
                            Index = 0,
                            Delta = new OpenAIChatDelta
                            {
                                Content = textElement.GetString() ?? string.Empty
                            }
                        }
                    ]
                }, cancellationToken);

                continue;
            }

            if (string.Equals(eventType, "content_block_start", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("content_block", out var block)
                && block.TryGetProperty("type", out var blockType)
                && string.Equals(blockType.GetString(), "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var blockIndex = root.TryGetProperty("index", out var indexNode) ? indexNode.GetInt32() : toolIndexes.Count;
                var toolIndex = toolIndexes.Count;
                toolIndexes[blockIndex] = toolIndex;

                await WriteOpenAiSseAsync(writer, new OpenAIChatCompletionChunk
                {
                    Id = completionId,
                    Created = created,
                    Model = model.OllamaModelName,
                    Choices =
                    [
                        new OpenAIChatChunkChoice
                        {
                            Index = 0,
                            Delta = new OpenAIChatDelta
                            {
                                ToolCalls =
                                [
                                    new OpenAIToolCall
                                    {
                                        Index = toolIndex,
                                        Id = block.TryGetProperty("id", out var idNode) ? idNode.GetString() ?? "tool-call" : "tool-call",
                                        Function = new OpenAIToolCallFunction
                                        {
                                            Name = block.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? string.Empty : string.Empty,
                                            Arguments = string.Empty
                                        }
                                    }
                                ]
                            }
                        }
                    ]
                }, cancellationToken);

                continue;
            }

            if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("delta", out var toolDelta)
                && toolDelta.TryGetProperty("type", out var toolDeltaType)
                && string.Equals(toolDeltaType.GetString(), "input_json_delta", StringComparison.OrdinalIgnoreCase))
            {
                var blockIndex = root.TryGetProperty("index", out var indexNode) ? indexNode.GetInt32() : 0;
                if (!toolIndexes.TryGetValue(blockIndex, out var toolIndex))
                {
                    toolIndex = toolIndexes.Count;
                    toolIndexes[blockIndex] = toolIndex;
                }

                await WriteOpenAiSseAsync(writer, new OpenAIChatCompletionChunk
                {
                    Id = completionId,
                    Created = created,
                    Model = model.OllamaModelName,
                    Choices =
                    [
                        new OpenAIChatChunkChoice
                        {
                            Index = 0,
                            Delta = new OpenAIChatDelta
                            {
                                ToolCalls =
                                [
                                    new OpenAIToolCall
                                    {
                                        Index = toolIndex,
                                        Id = string.Empty,
                                        Function = new OpenAIToolCallFunction
                                        {
                                            Name = string.Empty,
                                            Arguments = toolDelta.TryGetProperty("partial_json", out var partialJsonNode)
                                                ? partialJsonNode.GetString() ?? string.Empty
                                                : string.Empty
                                        }
                                    }
                                ]
                            }
                        }
                    ]
                }, cancellationToken);

                continue;
            }

            if (string.Equals(eventType, "message_delta", StringComparison.OrdinalIgnoreCase))
            {
                usage = MergeUsage(usage, ExtractUsage(root));
                var stopReason = root.TryGetProperty("delta", out var deltaNode)
                    && deltaNode.TryGetProperty("stop_reason", out var stopReasonNode)
                    ? stopReasonNode.GetString()
                    : null;

                await WriteOpenAiSseAsync(writer, new OpenAIChatCompletionChunk
                {
                    Id = completionId,
                    Created = created,
                    Model = model.OllamaModelName,
                    Choices =
                    [
                        new OpenAIChatChunkChoice
                        {
                            Index = 0,
                            Delta = new OpenAIChatDelta(),
                            FinishReason = MapFinishReason(stopReason, null)
                        }
                    ]
                }, cancellationToken);
            }
        }

        var mappedUsage = MapUsage(usage);
        if (mappedUsage is not null)
        {
            await WriteOpenAiSseAsync(writer, new OpenAIChatCompletionChunk
            {
                Id = completionId,
                Created = created,
                Model = model.OllamaModelName,
                Choices =
                [
                    new OpenAIChatChunkChoice
                    {
                        Index = 0,
                        Delta = new OpenAIChatDelta()
                    }
                ],
                Usage = mappedUsage
            }, cancellationToken);
        }

        await writer.WriteAsync("data: [DONE]\n\n");
        await writer.FlushAsync(cancellationToken);
    }

    private static OllamaChatChunkResponse CreateChunk(string model, string content, bool done, string? doneReason) =>
        new()
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Message = new OllamaChatMessage
            {
                Role = "assistant",
                Content = content
            },
            Done = done,
            DoneReason = doneReason
        };

    private static async Task WriteChunkAsync(StreamWriter writer, OllamaChatChunkResponse chunk, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(chunk, AppJsonContext.Default.OllamaChatChunkResponse).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private static OpenAIChatMessage CreateOpenAiMessage(IReadOnlyList<AnthropicContentBlock>? content)
    {
        var text = string.Concat(content?
            .Where(block => string.Equals(block.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(block => block.Text) ?? []);

        var toolCalls = content?
            .Where(block => string.Equals(block.Type, "tool_use", StringComparison.OrdinalIgnoreCase))
            .Select(block => new OpenAIToolCall
            {
                Id = block.Id ?? "tool-call",
                Function = new OpenAIToolCallFunction
                {
                    Name = block.Name ?? string.Empty,
                    Arguments = block.Input?.ToJsonString() ?? "{}"
                }
            })
            .ToArray();

        return new OpenAIChatMessage
        {
            Role = "assistant",
            Content = string.IsNullOrEmpty(text) ? null : JsonValue.Create(text),
            ToolCalls = toolCalls is { Length: > 0 } ? toolCalls : null
        };
    }

    private static string? MapFinishReason(string? stopReason, IReadOnlyList<OpenAIToolCall>? toolCalls)
    {
        if (toolCalls is { Count: > 0 })
        {
            return "tool_calls";
        }

        return stopReason switch
        {
            "end_turn" => "stop",
            "max_tokens" => "length",
            _ => stopReason
        };
    }

    private static OpenAIUsage? MapUsage(AnthropicUsage? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var promptTokens = (usage.InputTokens ?? 0)
            + (usage.CacheCreationInputTokens ?? 0)
            + (usage.CacheReadInputTokens ?? 0);
        var completionTokens = usage.OutputTokens ?? 0;

        if (promptTokens == 0 && completionTokens == 0)
        {
            return new OpenAIUsage();
        }

        return new OpenAIUsage
        {
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            TotalTokens = promptTokens + completionTokens
        };
    }

    private static AnthropicUsage ExtractUsage(JsonElement node)
    {
        if (!node.TryGetProperty("usage", out var usageNode))
        {
            return new AnthropicUsage();
        }

        return new AnthropicUsage
        {
            InputTokens = ReadInt32(usageNode, "input_tokens"),
            OutputTokens = ReadInt32(usageNode, "output_tokens"),
            CacheCreationInputTokens = ReadInt32(usageNode, "cache_creation_input_tokens"),
            CacheReadInputTokens = ReadInt32(usageNode, "cache_read_input_tokens")
        };
    }

    private static AnthropicUsage MergeUsage(AnthropicUsage current, AnthropicUsage update) =>
        new()
        {
            InputTokens = update.InputTokens ?? current.InputTokens,
            OutputTokens = update.OutputTokens ?? current.OutputTokens,
            CacheCreationInputTokens = update.CacheCreationInputTokens ?? current.CacheCreationInputTokens,
            CacheReadInputTokens = update.CacheReadInputTokens ?? current.CacheReadInputTokens
        };

    private static int? ReadInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var node))
        {
            return null;
        }

        return node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value)
            ? value
            : null;
    }

    private static string CreateCompletionId() => $"chatcmpl-{Guid.NewGuid():N}";

    private static async Task WriteOpenAiSseAsync(StreamWriter writer, OpenAIChatCompletionChunk chunk, CancellationToken cancellationToken)
    {
        await writer.WriteAsync($"data: {JsonSerializer.Serialize(chunk, AppJsonContext.Default.OpenAIChatCompletionChunk)}\n\n".AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
