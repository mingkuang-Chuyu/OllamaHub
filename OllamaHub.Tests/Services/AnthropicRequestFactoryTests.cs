using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Services;
using Xunit;

namespace OllamaHub.Tests.Services;

public sealed class AnthropicRequestFactoryTests
{
    [Fact]
    public void Create_MapsSystemAndChatMessages()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiModes = ["anthropic"],
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5",
            MaxTokens = 4096,
            Temperature = 0.2,
            TopP = 0.9,
            Extra = new Dictionary<string, JsonNode?>
            {
                ["service_tier"] = "standard_only"
            }
        };

        var request = new OllamaChatRequest
        {
            Model = "claude-sonnet-4-5",
            Stream = true,
            Options = new OllamaChatOptions
            {
                NumPredict = 2048
            },
            Messages =
            [
                new OllamaChatMessage { Role = "system", Content = "you are helpful" },
                new OllamaChatMessage { Role = "user", Content = "hello" },
                new OllamaChatMessage { Role = "assistant", Content = "hi" }
            ]
        };

        var result = factory.Create(model, request);

        Assert.Equal("claude-sonnet-4-5", result.Model);
        Assert.Equal("you are helpful", result.System);
        Assert.True(result.Stream);
        Assert.Equal(2048, result.MaxTokens);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("hello", result.Messages[0].Content[0].Text);
        Assert.Equal("assistant", result.Messages[1].Role);
        Assert.Equal("standard_only", Assert.IsAssignableFrom<JsonNode>(result.Extra["service_tier"])?.GetValue<string>());
    }

    [Fact]
    public void Create_MapsToolCallsAndToolResults()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiModes = ["anthropic"],
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5"
        };

        var request = new OllamaChatRequest
        {
            Model = "claude-sonnet-4-5",
            Messages =
            [
                new OllamaChatMessage
                {
                    Role = "assistant",
                    Content = "calling tool",
                    ToolCalls =
                    [
                        new OllamaToolCall
                        {
                            Id = "call_1",
                            Function = new OllamaToolCallFunction
                            {
                                Name = "read_file",
                                Arguments = JsonNode.Parse("""{"path":"README.md"}""")
                            }
                        }
                    ]
                },
                new OllamaChatMessage
                {
                    Role = "tool",
                    ToolCallId = "call_1",
                    ToolName = "read_file",
                    Content = "# README"
                }
            ]
        };

        var result = factory.Create(model, request);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("assistant", result.Messages[0].Role);
        Assert.Equal(2, result.Messages[0].Content.Count);
        Assert.Equal("text", result.Messages[0].Content[0].Type);
        Assert.Equal("tool_use", result.Messages[0].Content[1].Type);
        Assert.Equal("call_1", result.Messages[0].Content[1].Id);
        Assert.Equal("read_file", result.Messages[0].Content[1].Name);
        Assert.Equal("README.md", result.Messages[0].Content[1].Input?["path"]?.GetValue<string>());

        Assert.Equal("user", result.Messages[1].Role);
        Assert.Single(result.Messages[1].Content);
        Assert.Equal("tool_result", result.Messages[1].Content[0].Type);
        Assert.Equal("call_1", result.Messages[1].Content[0].ToolUseId);
        Assert.Equal("# README", result.Messages[1].Content[0].Content);
    }

    [Fact]
    public void Create_OpenAiRequest_MapsMessagesToolsAndExtra()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiModes = ["anthropic"],
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5",
            MaxTokens = 4096,
            Extra = new Dictionary<string, JsonNode?>
            {
                ["service_tier"] = "standard_only"
            }
        };

        var request = new OpenAIChatCompletionsRequest
        {
            Model = "claude-sonnet-4-5",
            Stream = true,
            Temperature = 0.4,
            TopP = 0.8,
            MaxTokens = 2048,
            ToolChoice = JsonValue.Create("auto"),
            Tools =
            [
                new OpenAIToolDefinition
                {
                    Function = new OpenAIToolFunctionDefinition
                    {
                        Name = "read_file",
                        Description = "Read a file",
                        Parameters = JsonNode.Parse("""{"type":"object"}""")
                    }
                }
            ],
            Messages =
            [
                new OpenAIChatMessage { Role = "system", Content = JsonValue.Create("you are helpful") },
                new OpenAIChatMessage { Role = "user", Content = JsonValue.Create("hello") },
                new OpenAIChatMessage
                {
                    Role = "assistant",
                    Content = JsonValue.Create("calling tool"),
                    ToolCalls =
                    [
                        new OpenAIToolCall
                        {
                            Id = "call_1",
                            Function = new OpenAIToolCallFunction
                            {
                                Name = "read_file",
                                Arguments = "{\"path\":\"README.md\"}"
                            }
                        }
                    ]
                },
                new OpenAIChatMessage
                {
                    Role = "tool",
                    ToolCallId = "call_1",
                    Name = "read_file",
                    Content = JsonValue.Create("# README")
                }
            ],
            Extra = new Dictionary<string, object?>
            {
                ["metadata"] = JsonDocument.Parse("""{"source":"test"}""").RootElement.Clone()
            }
        };

        var result = factory.Create(model, request);

        Assert.Equal("you are helpful", result.System);
        Assert.True(result.Stream);
        Assert.Equal(0.4, result.Temperature);
        Assert.Equal(0.8, result.TopP);
        Assert.Equal(2048, result.MaxTokens);
        Assert.Equal("auto", result.ToolChoice?["type"]?.GetValue<string>());
        Assert.Single(result.Tools!);
        Assert.Equal("read_file", result.Tools[0].Name);
        Assert.Equal("Read a file", result.Tools[0].Description);
        Assert.Equal("standard_only", Assert.IsAssignableFrom<JsonNode>(result.Extra["service_tier"])?.GetValue<string>());
        Assert.Equal("test", Assert.IsAssignableFrom<JsonNode>(result.Extra["metadata"])?["source"]?.GetValue<string>());
        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("tool_use", result.Messages[1].Content[1].Type);
        Assert.Equal("tool_result", result.Messages[2].Content[0].Type);
    }

    [Fact]
    public void Create_OpenAiJson_MapsMessagesToolsAndExtra()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiModes = ["anthropic"],
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5",
            MaxTokens = 4096,
            Extra = new Dictionary<string, JsonNode?>
            {
                ["service_tier"] = "standard_only"
            }
        };

        var request = JsonNode.Parse("""
        {
          "model": "claude-sonnet-4-5",
          "stream": true,
          "temperature": 0.4,
          "top_p": 0.8,
          "max_tokens": 2048,
          "tool_choice": "auto",
          "tools": [
            {
              "type": "function",
              "function": {
                "name": "read_file",
                "description": "Read a file",
                "parameters": { "type": "object" }
              }
            }
          ],
          "messages": [
            { "role": "system", "content": "you are helpful" },
            { "role": "user", "content": "hello" },
            {
              "role": "assistant",
              "content": "calling tool",
              "tool_calls": [
                {
                  "id": "call_1",
                  "function": {
                    "name": "read_file",
                    "arguments": "{\"path\":\"README.md\"}"
                  }
                }
              ]
            },
            {
              "role": "tool",
              "tool_call_id": "call_1",
              "name": "read_file",
              "content": "# README"
            }
          ],
          "metadata": {
            "source": "test"
          }
        }
        """)!.AsObject();

        var result = factory.Create(model, request);

        Assert.Equal("you are helpful", result.System);
        Assert.True(result.Stream);
        Assert.Equal(0.4, result.Temperature);
        Assert.Equal(0.8, result.TopP);
        Assert.Equal(2048, result.MaxTokens);
        Assert.Equal("auto", result.ToolChoice?["type"]?.GetValue<string>());
        Assert.Single(result.Tools!);
        Assert.Equal("read_file", result.Tools[0].Name);
        Assert.Equal("Read a file", result.Tools[0].Description);
        Assert.Equal("standard_only", Assert.IsAssignableFrom<JsonNode>(result.Extra["service_tier"])?.GetValue<string>());
        Assert.Equal("test", Assert.IsAssignableFrom<JsonNode>(result.Extra["metadata"])?["source"]?.GetValue<string>());
        Assert.Equal(3, result.Messages.Count);
        Assert.Equal("tool_use", result.Messages[1].Content[1].Type);
        Assert.Equal("tool_result", result.Messages[2].Content[0].Type);
    }

    [Fact]
    public void Create_OpenAiRequest_MapsRequiredToolChoiceToAnthropicAny()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiModes = ["anthropic"],
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5"
        };

        var request = new OpenAIChatCompletionsRequest
        {
            Model = "claude-sonnet-4-5",
            ToolChoice = JsonValue.Create("required"),
            Messages =
            [
                new OpenAIChatMessage { Role = "user", Content = JsonValue.Create("hello") }
            ]
        };

        var result = factory.Create(model, request);

        Assert.Equal("any", result.ToolChoice?["type"]?.GetValue<string>());
    }

    [Fact]
    public void Create_OpenAiRequest_MapsFunctionToolChoiceToAnthropicTool()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiModes = ["anthropic"],
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5"
        };

        var request = new OpenAIChatCompletionsRequest
        {
            Model = "claude-sonnet-4-5",
            ToolChoice = JsonNode.Parse("""{"type":"function","function":{"name":"read_file"}}"""),
            Messages =
            [
                new OpenAIChatMessage { Role = "user", Content = JsonValue.Create("hello") }
            ]
        };

        var result = factory.Create(model, request);

        Assert.Equal("tool", result.ToolChoice?["type"]?.GetValue<string>());
        Assert.Equal("read_file", result.ToolChoice?["name"]?.GetValue<string>());
    }

    [Fact]
    public void Create_OpenAiRequest_MapsMultimodalContentArray()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiModes = ["anthropic"],
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5"
        };

        var request = new OpenAIChatCompletionsRequest
        {
            Model = "claude-sonnet-4-5",
            Messages =
            [
                new OpenAIChatMessage
                {
                    Role = "user",
                    Content = JsonNode.Parse("""
                    [
                      {"type":"text","text":"describe image"},
                      {"type":"image_url","image_url":{"url":"data:image/png;base64,QUJDRA=="}}
                    ]
                    """)
                }
            ]
        };

        var result = factory.Create(model, request);

        var message = Assert.Single(result.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal(2, message.Content.Count);
        Assert.Equal("text", message.Content[0].Type);
        Assert.Equal("describe image", message.Content[0].Text);
        Assert.Equal("image", message.Content[1].Type);
        Assert.Equal("base64", message.Content[1].Source?.Type);
        Assert.Equal("image/png", message.Content[1].Source?.MediaType);
        Assert.Equal("QUJDRA==", message.Content[1].Source?.Data);
    }
}
