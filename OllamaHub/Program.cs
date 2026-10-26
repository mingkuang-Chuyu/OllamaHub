using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Interop;
using OllamaHub.Logging;
using OllamaHub.Services;

var configPath = Path.Combine(AppContext.BaseDirectory, OllamaHubConfigLoader.DefaultConfigFileName);
if (TryHandleCommand(args, configPath))
{
    Environment.Exit(0);
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
    options.SerializerOptions.PropertyNamingPolicy = null;
});

builder.Services.AddSingleton<IOllamaHubConfigProvider, OllamaHubConfigLoader>();
builder.Services.AddSingleton<IAnthropicRequestFactory, AnthropicRequestFactory>();
builder.Services.AddSingleton<IAnthropicResponseMapper, AnthropicResponseMapper>();
builder.Services.AddHttpClient<IAnthropicProxyClient, AnthropicProxyClient>();
builder.Services.AddHttpClient<IProtocolPassthroughClient, ProtocolPassthroughClient>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { name = "OllamaHub", status = "ok" }));
app.MapGet("/api/version", () => Results.Ok(new { version = "0.12.6" }));
app.MapGet("/api/ps", () => Results.Ok(new { models = Array.Empty<object>() }));

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
        ModelInfo = new Dictionary<string, object>
        {
            ["provider"] = model.ProviderId,
            ["anthropic_model"] = model.AnthropicModel,
            ["context_length"] = model.ContextLength,
            ["max_tokens"] = model.MaxTokens,
            ["capabilities"] = capabilities,
            ["vision"] = model.Vision,
        }
    });
});


app.MapPost("/v1/chat/completions", HandleChatCompletionsAsync);
app.MapPost("/openai/v1/chat/completions", HandleChatCompletionsAsync);

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
            Family = "",
            Families = [""],
            ParameterSize = "",
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
        StatusCodes.Status401Unauthorized => Results.Json(payload, statusCode: StatusCodes.Status401Unauthorized),
        StatusCodes.Status403Forbidden => Results.Json(payload, statusCode: StatusCodes.Status403Forbidden),
        StatusCodes.Status404NotFound => Results.NotFound(payload),
        StatusCodes.Status429TooManyRequests => Results.Json(payload, statusCode: StatusCodes.Status429TooManyRequests),
        _ => Results.Json(payload, statusCode: StatusCodes.Status502BadGateway)
    };
}

static bool TryGetString(JsonObject jsonObject, string propertyName, out string value)
{
    value = string.Empty;
    if (jsonObject[propertyName] is not JsonValue jsonValue
        || !jsonValue.TryGetValue<string>(out var stringValue)
        || string.IsNullOrWhiteSpace(stringValue))
    {
        return false;
    }

    value = stringValue;
    return true;
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

async Task<IResult> HandleChatCompletionsAsync(
    HttpContext httpContext,
    IOllamaHubConfigProvider configProvider,
    IAnthropicRequestFactory requestFactory,
    IAnthropicProxyClient proxyClient,
    IAnthropicResponseMapper responseMapper,
    IProtocolPassthroughClient passthroughClient,
    ILogger<Program> logger,
    JsonNode? requestJson,
    CancellationToken cancellationToken)
{
    if (requestJson is not JsonObject requestObject)
    {
        return Results.BadRequest(new OllamaErrorResponse
        {
            Error = "Request body must be a JSON object."
        });
    }

    if (!TryGetString(requestObject, "model", out var modelName))
    {
        return Results.BadRequest(new OllamaErrorResponse
        {
            Error = "Model name is required."
        });
    }

    var model = configProvider.FindModel(modelName);
    if (model is null)
    {
        logger.LogWarning(
            "OpenAI chat completion model not configured. Requested model: {RequestedModel}. Available models: {AvailableModels}",
            modelName,
            string.Join(", ", configProvider.GetModels().Select(m => m.OllamaModelName)));

        return Results.NotFound(new OllamaErrorResponse
        {
            Error = $"Model '{modelName}' is not configured."
        });
    }

    if (model.SupportsApiMode("openai"))
    {
        requestObject["model"] = model.ModelId;

        if (model.Extra != null && model.Extra.Count > 0)
        {
            foreach (var kvp in model.Extra)
            {
                requestObject[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        if (model.Temperature.HasValue && !requestObject.ContainsKey("temperature"))
            requestObject["temperature"] = model.Temperature.Value;

        if (model.TopP.HasValue && !requestObject.ContainsKey("top_p"))
            requestObject["top_p"] = model.TopP.Value;

        await passthroughClient.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", requestObject, cancellationToken);
        return Results.Empty;
    }

    var anthropicRequest = requestFactory.Create(model, requestObject);

    if (!anthropicRequest.Stream)
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
}