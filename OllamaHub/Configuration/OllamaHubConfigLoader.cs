using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using OllamaHub.Serialization;

namespace OllamaHub.Configuration;

public interface IOllamaHubConfigProvider
{
    string ConfigPath { get; }

    ResolvedAppConfig GetConfig();

    IReadOnlyList<ResolvedModelConfig> GetModels();

    ResolvedModelConfig? FindModel(string modelName);
}

public static class ProtectedApiKeyStore
{
    public const string Prefix = "dpapi:";

    public static bool IsProtectedValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static string Protect(string plainText)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected API key storage is only supported on Windows.");
        }

        var payload = Encoding.UTF8.GetBytes(plainText);
        var protectedBytes = ProtectForCurrentUser(payload);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string value)
    {
        if (!IsProtectedValue(value))
        {
            return value;
        }

        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected API key storage is only supported on Windows.");
        }

        var protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
        var payload = UnprotectForCurrentUser(protectedBytes);
        return Encoding.UTF8.GetString(payload);
    }

    private static byte[] ProtectForCurrentUser(byte[] payload)
    {
        var input = DATA_BLOB.From(payload);
        var output = new DATA_BLOB();

        try
        {
            if (!CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return output.ToArray();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    private static byte[] UnprotectForCurrentUser(byte[] payload)
    {
        var input = DATA_BLOB.From(payload);
        var output = new DATA_BLOB();

        try
        {
            if (!CryptUnprotectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out output))
            {
                throw new CryptographicException(Marshal.GetLastWin32Error());
            }

            return output.ToArray();
        }
        finally
        {
            input.Free();
            output.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;

        public static DATA_BLOB From(byte[] data)
        {
            var blob = new DATA_BLOB
            {
                cbData = data.Length,
                pbData = Marshal.AllocHGlobal(data.Length)
            };

            Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }

        public readonly byte[] ToArray()
        {
            if (cbData <= 0 || pbData == IntPtr.Zero)
            {
                return [];
            }

            var data = new byte[cbData];
            Marshal.Copy(pbData, data, 0, cbData);
            return data;
        }

        public void Free()
        {
            if (pbData != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(pbData);
                pbData = IntPtr.Zero;
                cbData = 0;
            }
        }
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        string? ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);
}

public sealed class OllamaHubConfigLoader : IOllamaHubConfigProvider
{
    public const string DefaultConfigFileName = "settings.json";

    private readonly IReadOnlyList<ResolvedModelConfig> _models;
    private readonly ResolvedAppConfig _config;

    public OllamaHubConfigLoader(ILogger<OllamaHubConfigLoader> logger)
    {
        ConfigPath = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
        _config = Load(ConfigPath, logger);
        _models = _config.Models;
    }

    public string ConfigPath { get; }

    public ResolvedAppConfig GetConfig() => _config;

    public IReadOnlyList<ResolvedModelConfig> GetModels() => _models;

    public ResolvedModelConfig? FindModel(string modelName) => FindModel(_models, modelName);

    internal static ResolvedAppConfig LoadConfig(string configPath, ILogger logger) => Load(configPath, logger);

    internal static IReadOnlyList<ResolvedModelConfig> LoadModels(string configPath, ILogger logger) => Load(configPath, logger).Models;

    internal static ResolvedServerConfig LoadServer(string configPath, ILogger logger) => Load(configPath, logger).Server;

    internal static LoggingConfig LoadLogging(string configPath, ILogger logger) => Load(configPath, logger).Logging;

    internal static OllamaHubConfig LoadRawConfig(string configPath)
    {
        using var stream = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize(stream, AppJsonContext.Default.OllamaHubConfig);
        return config ?? new OllamaHubConfig();
    }

    internal static void SetProtectedApiKey(string configPath, string target, string protectedApiKey)
    {
        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonObject root;
        if (File.Exists(configPath) && !string.IsNullOrWhiteSpace(File.ReadAllText(configPath)))
        {
            root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject
                ?? throw new InvalidOperationException("settings.json root must be a JSON object.");
        }
        else
        {
            throw new FileNotFoundException("Config file not found.", configPath);
        }

        if (TrySetProtectedApiKey(root["providers"] as JsonArray, "id", target, protectedApiKey))
        {
            WriteConfigFile(configPath, root);
            return;
        }

        if (TrySetProtectedApiKey(root["models"] as JsonArray, "id", target, protectedApiKey))
        {
            WriteConfigFile(configPath, root);
            return;
        }

        throw new InvalidOperationException($"No provider or model with id '{target}' was found in settings.json.");
    }

    internal static ResolvedModelConfig? FindModel(IReadOnlyList<ResolvedModelConfig> models, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var normalizedModelName = modelName.Trim();
        var exactOllamaMatch = models.FirstOrDefault(model =>
            string.Equals(model.OllamaModelName, normalizedModelName, StringComparison.OrdinalIgnoreCase));

        if (exactOllamaMatch is not null)
        {
            return exactOllamaMatch;
        }

        var displayNameMatch = models.FirstOrDefault(model =>
            string.Equals(model.DisplayName, normalizedModelName, StringComparison.OrdinalIgnoreCase));

        if (displayNameMatch is not null)
        {
            return displayNameMatch;
        }

        return models.FirstOrDefault(model =>
            string.Equals(model.ModelId, normalizedModelName, StringComparison.OrdinalIgnoreCase));
    }

    private static void WriteConfigFile(string configPath, JsonObject root)
    {
        using var stream = File.Create(configPath);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        root.WriteTo(writer);
    }

    private static bool TrySetProtectedApiKey(JsonArray? items, string idPropertyName, string target, string protectedApiKey)
    {
        if (items is null)
        {
            return false;
        }

        foreach (var node in items)
        {
            if (node is not JsonObject item)
            {
                continue;
            }

            var id = item[idPropertyName]?.GetValue<string>();
            if (!string.Equals(id, target, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            item["protectedApiKey"] = protectedApiKey;
            return true;
        }

        return false;
    }

    private static ResolvedAppConfig Load(string configPath, ILogger logger)
    {
        if (!File.Exists(configPath))
        {
            logger.LogWarning("Config file not found: {ConfigPath}", configPath);
            return new ResolvedAppConfig();
        }

        using var stream = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize(stream, AppJsonContext.Default.OllamaHubConfig);

        if (config is null)
        {
            logger.LogWarning("Config file is empty or invalid: {ConfigPath}", configPath);
            return new ResolvedAppConfig();
        }

        var server = new ResolvedServerConfig
        {
            Urls = ResolveServerUrls(config)
        };

        var providers = (config.Providers ?? []).ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        var models = new List<ResolvedModelConfig>();

        foreach (var model in config.Models ?? [])
        {
            var providerId = GetProviderId(model);
            providers.TryGetValue(providerId, out var provider);

            var apiModes = ResolveApiModes(model.ApiMode, provider?.ApiMode);
            if (!apiModes.Contains("anthropic", StringComparer.OrdinalIgnoreCase)
                && !apiModes.Contains("openai", StringComparer.OrdinalIgnoreCase)
                && !apiModes.Contains("ollama", StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseUrl = model.BaseUrl ?? provider?.BaseUrl ?? config.BaseUrl;
            var apiKey = ResolveApiKey(model.ApiKey, model.ProtectedApiKey, provider?.ApiKey, provider?.ProtectedApiKey);
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogWarning("Skipping model {ModelId} because baseUrl or apiKey is missing.", model.Id);
                continue;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (provider is not null)
            {
                foreach (var pair in provider.Headers ?? new Dictionary<string, string>())
                {
                    headers[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in model.Headers ?? new Dictionary<string, string>())
            {
                headers[pair.Key] = pair.Value;
            }

            models.Add(new ResolvedModelConfig
            {
                ModelId = model.Id,
                AnthropicModel = model.Id,
                ProviderId = providerId,
                ApiModes = apiModes,
                BaseUrl = baseUrl.TrimEnd('/'),
                ApiKey = apiKey,
                DisplayName = model.DisplayName ?? model.Id,
                OllamaModelName = BuildOllamaModelName(model),
                Family = model.Family ?? "claude",
                ContextLength = model.ContextLength ?? 128000,
                MaxTokens = model.MaxTokens ?? 4096,
                Vision = model.Vision,
                Temperature = model.Temperature,
                TopP = model.TopP,
                Headers = headers,
                Extra = new Dictionary<string, JsonNode?>(model.Extra ?? new Dictionary<string, JsonNode?>(), StringComparer.OrdinalIgnoreCase)
            });
        }

        logger.LogInformation("Loaded {Count} model(s) from {ConfigPath}", models.Count, configPath);
        return new ResolvedAppConfig
        {
            Server = server,
            Logging = config.Logging ?? new LoggingConfig(),
            Models = models
        };
    }

    private static IReadOnlyList<string> ResolveApiModes(string? modelApiMode, string? providerApiMode)
    {
        var raw = string.IsNullOrWhiteSpace(modelApiMode) ? providerApiMode : modelApiMode;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ["openai"];
        }

        var modes = raw
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(mode => !string.IsNullOrWhiteSpace(mode))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return modes.Length > 0 ? modes : ["openai"];
    }

    private static string? ResolveApiKey(string? modelApiKey, string? modelProtectedApiKey, string? providerApiKey, string? providerProtectedApiKey)
    {
        return TryResolveConfiguredApiKey(modelApiKey, modelProtectedApiKey)
            ?? TryResolveConfiguredApiKey(providerApiKey, providerProtectedApiKey);
    }

    private static string? TryResolveConfiguredApiKey(string? plainApiKey, string? protectedApiKey)
    {
        if (!string.IsNullOrWhiteSpace(protectedApiKey))
        {
            return ProtectedApiKeyStore.Unprotect(protectedApiKey);
        }

        if (!string.IsNullOrWhiteSpace(plainApiKey))
        {
            return ProtectedApiKeyStore.Unprotect(plainApiKey);
        }

        return null;
    }

    public static string BuildDigest(ResolvedModelConfig model)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{model.ProviderId}:{model.ModelId}:{model.OllamaModelName}"));
        return Convert.ToHexStringLower(bytes);
    }

    private static string GetProviderId(ModelConfig model)
    {
        return model.OwnedBy ?? model.Provider ?? model.Provide ?? "default";
    }

    private static string BuildOllamaModelName(ModelConfig model)
    {
        var Name = model.DisplayName ?? model.Id;

        return string.IsNullOrWhiteSpace(model.ConfigId)
            ? Name
            : $"{Name}::{model.ConfigId}";
    }

    private static IReadOnlyList<string> ResolveServerUrls(OllamaHubConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Url))
        {
            return [config.Url];
        }

        if (!string.IsNullOrWhiteSpace(config.Host) && config.Port is > 0)
        {
            return [$"http://{config.Host}:{config.Port}"];
        }

        return [];
    }
}
