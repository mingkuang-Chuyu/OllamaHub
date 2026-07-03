using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Serialization;
using OllamaHub.Services;
using Xunit;

namespace OllamaHub.Tests.Services;

public sealed class ProtocolPassthroughClientTests
{
    [Fact]
    public async Task ProxyAsync_OpenAiRequest_SendsConfiguredModelId()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProtocolPassthroughClient(httpClient, NullLogger<ProtocolPassthroughClient>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Response.Body = new MemoryStream();

        var model = new ResolvedModelConfig
        {
            ModelId = "deepseek/deepseek-v4-pro",
            OllamaModelName = "360智脑/deepseek-v4-pro",
            DisplayName = "360智脑/deepseek-v4-pro",
            ProviderId = "360智脑",
            ApiModes = ["openai"],
            BaseUrl = "https://api.360.cn",
            ApiKey = "secret",
            AnthropicModel = "deepseek/deepseek-v4-pro"
        };

        var payload = new OpenAIChatCompletionsRequest
        {
            Model = model.ModelId,
            Messages =
            [
                new OpenAIChatMessage
                {
                    Role = "user",
                    Content = JsonValue.Create("hello")
                }
            ],
            Stream = false
        };

        await client.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", payload, AppJsonContext.Default.OpenAIChatCompletionsRequest, CancellationToken.None);

        Assert.NotNull(handler.RequestBody);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("deepseek/deepseek-v4-pro", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProxyAsync_OllamaRequest_SendsConfiguredModelId()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProtocolPassthroughClient(httpClient, NullLogger<ProtocolPassthroughClient>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Response.Body = new MemoryStream();

        var model = new ResolvedModelConfig
        {
            ModelId = "deepseek/deepseek-v4-pro",
            OllamaModelName = "360智脑/deepseek-v4-pro",
            DisplayName = "360智脑/deepseek-v4-pro",
            ProviderId = "360智脑",
            ApiModes = ["ollama"],
            BaseUrl = "https://api.360.cn",
            ApiKey = "secret",
            AnthropicModel = "deepseek/deepseek-v4-pro"
        };

        var payload = new OllamaChatRequest
        {
            Model = model.ModelId,
            Messages =
            [
                new OllamaChatMessage
                {
                    Role = "user",
                    Content = "hello"
                }
            ],
            Stream = false
        };

        await client.ProxyAsync(httpContext, model, "ollama", "/api/chat", payload, AppJsonContext.Default.OllamaChatRequest, CancellationToken.None);

        Assert.NotNull(handler.RequestBody);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("deepseek/deepseek-v4-pro", json.RootElement.GetProperty("model").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
