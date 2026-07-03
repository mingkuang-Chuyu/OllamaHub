using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Interop;
using OllamaHub.Logging;
using OllamaHub.Serialization;
using OllamaHub.Services;

var configPath = Path.Combine(AppContext.BaseDirectory, OllamaHubConfigLoader.DefaultConfigFileName);
if (TryHandleCommand(args, configPath))
{
    return;
}

var builder = WebApplication.CreateBuilder(args);
var logPath = Path.Combine(AppContext.BaseDirectory, "OllamaHub.log");

var appConfig = OllamaHubConfigLoader.LoadConfig(configPath, NullLogger.Instance);
var minLogLevel = appConfig.Logging.GetLogLevel();
var enableConsoleLogging = WindowsConsoleManager.ShouldEnableConsole(minLogLevel);

if (enableConsoleLogging)
{
    WindowsConsoleManager.EnsureConsole();
}

using var startupLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.ClearProviders();
    logging.AddProvider(new FileLoggerProvider(logPath, minLogLevel));

    if (enableConsoleLogging)
    {
        logging.AddSimpleConsole();
    }
});

var startupLogger = startupLoggerFactory.CreateLogger("Startup");
startupLogger.LogInformation("Loaded configuration from {ConfigPath}", configPath);

builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(logPath, minLogLevel));

if (enableConsoleLogging)
{
    builder.Logging.AddSimpleConsole();
}

if (appConfig.Server.Urls.Count > 0)
{
    builder.WebHost.UseUrls(appConfig.Server.Urls.ToArray());
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.TypeInfoResolver = AppJsonContext.Default;
    options.SerializerOptions.PropertyNamingPolicy = null;
});

builder.Services.AddSingleton<IOllamaHubConfigProvider, OllamaHubConfigLoader>();
builder.Services.AddSingleton<IAnthropicRequestFactory, AnthropicRequestFactory>();
builder.Services.AddSingleton<IAnthropicResponseMapper, AnthropicResponseMapper>();
builder.Services.AddHttpClient<IAnthropicProxyClient, AnthropicProxyClient>();
builder.Services.AddHttpClient<IProtocolPassthroughClient, ProtocolPassthroughClient>();

var app = builder.Build();

app.MapGet("/", () => Results.Json(new RootStatusResponse
{
    Name = "OllamaHub",
    Status = "ok"
}, AppJsonContext.Default.RootStatusResponse));
app.MapGet("/api/version", () => Results.Json(new VersionResponse
{
    Version = "0.12.6"
}, AppJsonContext.Default.VersionResponse));
app.MapGet("/api/ps", () => Results.Json(new OllamaProcessListResponse
{
    Models = []
}, AppJsonContext.Default.OllamaProcessListResponse));

app.MapGet("/api/tags", (IOllamaHubConfigProvider configProvider) =>
    Results.Ok(new OllamaTagListResponse
    {
        Models = configProvider.GetModels().Select(ToDescriptor).ToArray()
    }));

app.MapPost("/api/show", (IOllamaHubConfigProvider configProvider, OllamaShowRequest request) =>
{
    var modelName = request.Model;
    if (string.IsNullOrWhiteSpace(modelName))
    {
        return Results.BadRequest(new OllamaErrorResponse
        {
            Error = "Model name is required."
        });
    }

    var model = configProvider.FindModel(modelName);
    if (model is null)
    {
        return Results.NotFound(new OllamaErrorResponse
        {
            Error = $"Model '{modelName}' is not configured."
        });
    }

    var capabilities = model.Vision
        ? new[] { "completion", "tools", "vision" }
        : new[] { "completion", "tools" };

    return Results.Ok(new OllamaShowResponse
    {
        Modelfile = $"FROM {model.AnthropicModel}",
        Parameters = $"family={model.Family}\ncontext_length={model.ContextLength}\nmax_tokens={model.MaxTokens}",
        Details = ToDescriptor(model).Details,
        Capabilities = capabilities,
        ModelInfo = new Dictionary<string, JsonNode?>
        {
            ["provider"] = JsonValue.Create(model.ProviderId),
            ["anthropic_model"] = JsonValue.Create(model.AnthropicModel),
            ["context_length"] = JsonValue.Create(model.ContextLength),
            ["max_tokens"] = JsonValue.Create(model.MaxTokens),
            ["capabilities"] = new JsonArray(capabilities.Select(capability => JsonValue.Create(capability)!).ToArray()),
            ["vision"] = JsonValue.Create(model.Vision),
        }
    });
});

app.MapPost("/api/chat", async (
    HttpContext httpContext,
    IOllamaHubConfigProvider configProvider,
    IAnthropicRequestFactory requestFactory,
    IAnthropicProxyClient proxyClient,
    IAnthropicResponseMapper responseMapper,
    IProtocolPassthroughClient passthroughClient,
    OllamaChatRequest request,
    CancellationToken cancellationToken) =>
{
    var model = configProvider.FindModel(request.Model);
    if (model is null)
    {
        return Results.NotFound(new OllamaErrorResponse
        {
            Error = $"Model '{request.Model}' is not configured."
        });
    }

    if (model.SupportsApiMode("ollama"))
    {
        var upstreamRequest = new OllamaChatRequest
        {
            Model = model.ModelId,
            Messages = request.Messages,
            Stream = request.Stream,
            Options = request.Options
        };

        await passthroughClient.ProxyAsync(httpContext, model, "ollama", "/api/chat", upstreamRequest, AppJsonContext.Default.OllamaChatRequest, cancellationToken);
        return Results.Empty;
    }

    var anthropicRequest = requestFactory.Create(model, request);

    if (!request.Stream)
    {
        var (statusCode, response, error) = await proxyClient.SendAsync(model, anthropicRequest, cancellationToken);
        if (response is null)
        {
            return ToError(statusCode, error);
        }

        return Results.Ok(responseMapper.MapMessageResponse(model, response));
    }

    var streamResult = await proxyClient.SendStreamAsync(model, anthropicRequest, cancellationToken);
    if (streamResult.Stream is null)
    {
        return ToError(streamResult.StatusCode, streamResult.Error);
    }

    httpContext.Response.StatusCode = StatusCodes.Status200OK;
    httpContext.Response.ContentType = "application/x-ndjson";

    await using var anthropicStream = streamResult.Stream;
    await responseMapper.WriteStreamAsync(model, anthropicStream, httpContext.Response.Body, cancellationToken);
    return Results.Empty;
});

app.MapPost("/v1/chat/completions", async (
    HttpContext httpContext,
    IOllamaHubConfigProvider configProvider,
    IAnthropicRequestFactory requestFactory,
    IAnthropicProxyClient proxyClient,
    IAnthropicResponseMapper responseMapper,
    IProtocolPassthroughClient passthroughClient,
    ILogger<Program> logger,
    OpenAIChatCompletionsRequest request,
    CancellationToken cancellationToken) =>
{
    var model = configProvider.FindModel(request.Model);
    if (model is null)
    {
        logger.LogWarning(
            "OpenAI chat completion model not configured. Requested model: {RequestedModel}. Available models: {AvailableModels}",
            request.Model,
            string.Join(", ", configProvider.GetModels().Select(m => m.OllamaModelName)));

        return Results.NotFound(new OllamaErrorResponse
        {
            Error = $"Model '{request.Model}' is not configured."
        });
    }

    if (model.SupportsApiMode("openai"))
    {
        var upstreamRequest = new OpenAIChatCompletionsRequest
        {
            Model = model.ModelId,
            Messages = request.Messages,
            Stream = request.Stream,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxTokens = request.MaxTokens,
            Tools = request.Tools,
            ToolChoice = request.ToolChoice,
            Extra = request.Extra
        };
        await passthroughClient.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", upstreamRequest, AppJsonContext.Default.OpenAIChatCompletionsRequest, cancellationToken);
        return Results.Empty;
    }

    var anthropicRequest = requestFactory.Create(model, request);

    if (!request.Stream)
    {
        var (statusCode, response, error) = await proxyClient.SendAsync(model, anthropicRequest, cancellationToken);
        if (response is null)
        {
            return ToError(statusCode, error);
        }

        return Results.Ok(responseMapper.MapOpenAiResponse(model, response));
    }

    var streamResult = await proxyClient.SendStreamAsync(model, anthropicRequest, cancellationToken);
    if (streamResult.Stream is null)
    {
        return ToError(streamResult.StatusCode, streamResult.Error);
    }

    httpContext.Response.StatusCode = StatusCodes.Status200OK;
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";

    await using var anthropicStream = streamResult.Stream;
    await responseMapper.WriteOpenAiStreamAsync(model, anthropicStream, httpContext.Response.Body, cancellationToken);
    return Results.Empty;
});

app.MapFallback((HttpContext httpContext, ILogger<Program> logger) =>
{
    if (HttpMethods.IsPost(httpContext.Request.Method))
    {
        logger.LogError("Unrecognized POST route: {Path}", httpContext.Request.Path.Value);
    }

    return Results.NotFound(new OllamaErrorResponse
    {
        Error = $"Route '{httpContext.Request.Path.Value}' is not recognized."
    });
});

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var configuredUrls = app.Services.GetRequiredService<IOllamaHubConfigProvider>().GetConfig().Server.Urls;

    if (configuredUrls.Count > 0)
    {
        logger.LogInformation("OllamaHub listening on configured URLs: {Urls}", string.Join(", ", configuredUrls));
    }
    else
    {
        logger.LogInformation("OllamaHub listening on default ASP.NET Core URLs.");
    }
});

app.Run();

static OllamaModelDescriptor ToDescriptor(ResolvedModelConfig model) =>
    new()
    {
        Name = model.OllamaModelName,
        Model = model.ModelId,
        ModifiedAt = DateTimeOffset.UtcNow.ToString("O"),
        Size = 0,
        Digest = OllamaHubConfigLoader.BuildDigest(model),
        Details = new OllamaModelDetails
        {
            Family = model.Family,
            Families = [model.Family],
            ParameterSize = model.ContextLength.ToString(),
            QuantizationLevel = "proxy"
        }
    };

static IResult ToError(HttpStatusCode statusCode, string? error)
{
    var payload = new OllamaErrorResponse
    {
        Error = error ?? "Upstream request failed."
    };

    return (int)statusCode switch
    {
        StatusCodes.Status400BadRequest => Results.BadRequest(payload),
        StatusCodes.Status401Unauthorized => Results.Json(payload, AppJsonContext.Default.OllamaErrorResponse, statusCode: StatusCodes.Status401Unauthorized),
        StatusCodes.Status403Forbidden => Results.Json(payload, AppJsonContext.Default.OllamaErrorResponse, statusCode: StatusCodes.Status403Forbidden),
        StatusCodes.Status404NotFound => Results.NotFound(payload),
        StatusCodes.Status429TooManyRequests => Results.Json(payload, AppJsonContext.Default.OllamaErrorResponse, statusCode: StatusCodes.Status429TooManyRequests),
        _ => Results.Json(payload, AppJsonContext.Default.OllamaErrorResponse, statusCode: StatusCodes.Status502BadGateway)
    };
}

static bool TryHandleCommand(string[] args, string configPath)
{
    if (args.Length == 0)
    {
        return false;
    }

    if (!string.Equals(args[0], "SetApiKey", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    WindowsConsoleManager.EnsureConsole();

    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: OllamaHub SetApiKey <providerOrModelId> <apiKey>");
        return true;
    }

    try
    {
        var target = args[1];
        var apiKey = args[2];
        SetProtectedApiKey(configPath, target, apiKey);
        Console.WriteLine($"API Key for '{target}' has been stored securely.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
    }

    return true;
}

static void SetProtectedApiKey(string configPath, string target, string apiKey)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("SetApiKey is only supported on Windows.");
    }

    if (string.IsNullOrWhiteSpace(target))
    {
        throw new ArgumentException("Target provider or model id is required.", nameof(target));
    }

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new ArgumentException("API key is required.", nameof(apiKey));
    }

    if (!File.Exists(configPath))
    {
        throw new FileNotFoundException("Config file not found.", configPath);
    }

    var protectedApiKey = ProtectedApiKeyStore.Protect(apiKey);
    OllamaHubConfigLoader.SetProtectedApiKey(configPath, target, protectedApiKey);
}
