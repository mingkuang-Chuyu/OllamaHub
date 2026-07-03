using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using OllamaHub.Configuration;
using OllamaHub.Contracts;

namespace OllamaHub.Serialization;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(OllamaHubConfig))]
[JsonSerializable(typeof(RootStatusResponse))]
[JsonSerializable(typeof(VersionResponse))]
[JsonSerializable(typeof(OllamaProcessListResponse))]
[JsonSerializable(typeof(OllamaTagListResponse))]
[JsonSerializable(typeof(OllamaShowRequest))]
[JsonSerializable(typeof(OllamaShowResponse))]
[JsonSerializable(typeof(AnthropicMessagesRequest))]
[JsonSerializable(typeof(AnthropicMessagesResponse))]
[JsonSerializable(typeof(AnthropicErrorEnvelope))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatChunkResponse))]
[JsonSerializable(typeof(OpenAIChatCompletionsRequest))]
[JsonSerializable(typeof(OpenAIChatCompletionsResponse))]
[JsonSerializable(typeof(OpenAIChatCompletionChunk))]
[JsonSerializable(typeof(OllamaErrorResponse))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
public partial class AppJsonContext : JsonSerializerContext
{
}
