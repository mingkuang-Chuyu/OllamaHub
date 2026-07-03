using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace OllamaHub.Configuration;

public sealed class OllamaHubConfig
{
    [JsonPropertyName("logging")]
    public LoggingConfig? Logging { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("port")]
    public int? Port { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("providers")]
    public IReadOnlyList<ProviderConfig> Providers { get; init; } = [];

    [JsonPropertyName("models")]
    public IReadOnlyList<ModelConfig> Models { get; init; } = [];
}

public sealed class ProviderConfig
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("protectedApiKey")]
    public string? ProtectedApiKey { get; init; }

    [JsonPropertyName("apiMode")]
    public string? ApiMode { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelConfig
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; init; }

    [JsonPropertyName("configId")]
    public string? ConfigId { get; init; }

    [JsonPropertyName("family")]
    public string? Family { get; init; }

    [JsonPropertyName("owned_by")]
    public string? OwnedBy { get; init; }

    [JsonPropertyName("provider")]
    public string? Provider { get; init; }

    [JsonPropertyName("provide")]
    public string? Provide { get; init; }

    [JsonPropertyName("baseUrl")]
    public string? BaseUrl { get; init; }

    [JsonPropertyName("apiKey")]
    public string? ApiKey { get; init; }

    [JsonPropertyName("protectedApiKey")]
    public string? ProtectedApiKey { get; init; }

    [JsonPropertyName("apiMode")]
    public string? ApiMode { get; init; }

    [JsonPropertyName("context_length")]
    public int? ContextLength { get; init; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; init; }

    [JsonPropertyName("vision")]
    public bool Vision { get; init; }

    [JsonPropertyName("temperature")]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; init; }

    [JsonPropertyName("headers")]
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("extra")]
    public Dictionary<string, JsonNode?> Extra { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ResolvedModelConfig
{
    public required string ModelId { get; init; }

    public required string OllamaModelName { get; init; }

    public required string DisplayName { get; init; }

    public required string ProviderId { get; init; }

    public IReadOnlyList<string> ApiModes { get; init; } = [];

    public required string BaseUrl { get; init; }

    public required string ApiKey { get; init; }

    public required string AnthropicModel { get; init; }

    public string Family { get; init; } = "claude";

    public int ContextLength { get; init; } = 128000;

    public int MaxTokens { get; init; } = 4096;

    public bool Vision { get; init; }

    public double? Temperature { get; init; }

    public double? TopP { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, JsonNode?> Extra { get; init; } = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);

    public bool SupportsApiMode(string apiMode) =>
        ApiModes.Any(mode => string.Equals(mode, apiMode, StringComparison.OrdinalIgnoreCase));
}

public sealed class ResolvedServerConfig
{
    public IReadOnlyList<string> Urls { get; init; } = [];
}

public sealed class ResolvedAppConfig
{
    public ResolvedServerConfig Server { get; init; } = new();

    public LoggingConfig Logging { get; init; } = new();

    public IReadOnlyList<ResolvedModelConfig> Models { get; init; } = [];
}

public sealed class LoggingConfig
{
    [JsonPropertyName("level")]
    public string? Level { get; init; }

    public LogLevel GetLogLevel()
    {
        return Level?.Trim().ToLowerInvariant() switch
        {
            "none" => LogLevel.None,
            "error" => LogLevel.Error,
            "warning" => LogLevel.Warning,
            "info" => LogLevel.Information,
            _ => LogLevel.Information, // 默认应该用 Information 等级，这样才能在首次启动无任何配置的情况下，看到控制台日志输出内容
        };
    }
}