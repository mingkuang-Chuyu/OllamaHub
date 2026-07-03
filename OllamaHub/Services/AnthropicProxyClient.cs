using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Serialization;

namespace OllamaHub.Services;

public interface IAnthropicProxyClient
{
    Task<(HttpStatusCode StatusCode, AnthropicMessagesResponse? Response, string? Error)> SendAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken);

    Task<(HttpStatusCode StatusCode, Stream? Stream, string? Error)> SendStreamAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken);
}

public sealed class AnthropicProxyClient(HttpClient httpClient, ILogger<AnthropicProxyClient> logger) : IAnthropicProxyClient
{
    public async Task<(HttpStatusCode StatusCode, AnthropicMessagesResponse? Response, string? Error)> SendAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken)
    {
        using var message = BuildRequestMessage(model, request);
        var requestBody = await message.Content!.ReadAsStringAsync(cancellationToken);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError(
                "Anthropic request failed {Path}. RequestBody: {RequestBody}. Response ({StatusCode}, {ContentType}): {ResponseBody}",
                message.RequestUri?.AbsolutePath ?? "/v1/messages",
                requestBody,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "application/json",
                body);
            return (response.StatusCode, null, ReadError(body, response.StatusCode));
        }

        logger.LogInformation(
            "Anthropic response {Path} ({StatusCode}, {ContentType}): {ResponseBody}",
            message.RequestUri?.AbsolutePath ?? "/v1/messages",
            (int)response.StatusCode,
            response.Content.Headers.ContentType?.ToString() ?? "application/json",
            body);

        var result = JsonSerializer.Deserialize(body, AppJsonContext.Default.AnthropicMessagesResponse);
        if (result is null)
        {
            return (HttpStatusCode.BadGateway, null, "Anthropic 返回了空响应。");
        }

        return (response.StatusCode, result, null);
    }

    public async Task<(HttpStatusCode StatusCode, Stream? Stream, string? Error)> SendStreamAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken)
    {
        var message = BuildRequestMessage(model, request);
        var requestBody = await message.Content!.ReadAsStringAsync(cancellationToken);
        var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogError(
                "Anthropic request failed {Path}. RequestBody: {RequestBody}. Response ({StatusCode}, {ContentType}): {ResponseBody}",
                message.RequestUri?.AbsolutePath ?? "/v1/messages",
                requestBody,
                (int)response.StatusCode,
                response.Content.Headers.ContentType?.ToString() ?? "application/json",
                body);

            var error = ReadError(body, response.StatusCode);
            response.Dispose();
            message.Dispose();
            return (response.StatusCode, null, error);
        }

        message.Dispose();
        return (response.StatusCode, await response.Content.ReadAsStreamAsync(cancellationToken), null);
    }

    private static HttpRequestMessage BuildRequestMessage(ResolvedModelConfig model, AnthropicMessagesRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl}/v1/messages");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.Add("x-api-key", model.ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");

        var payload = JsonSerializer.Serialize(request, AppJsonContext.Default.AnthropicMessagesRequest);
        message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        foreach (var header in model.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return message;
    }

    private static string ReadError(string body, HttpStatusCode statusCode)
    {
        try
        {
            var error = JsonSerializer.Deserialize(body, AppJsonContext.Default.AnthropicErrorEnvelope);
            return error?.Error?.Message ?? $"Anthropic 请求失败，状态码 {(int)statusCode}。";
        }
        catch
        {
            return $"Anthropic 请求失败，状态码 {(int)statusCode}。";
        }
    }
}
