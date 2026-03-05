using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;
using SAGIDE.Service.Routing;

namespace SAGIDE.Service.Providers;

public class ProviderFactory
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeoutConfig _timeoutConfig;
    private readonly OllamaHostHealthMonitor? _ollamaHealthMonitor;
    private readonly Dictionary<ModelProvider, IAgentProvider> _providers = new();

    public ProviderFactory(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        TimeoutConfig timeoutConfig,
        OllamaHostHealthMonitor? ollamaHealthMonitor = null)
    {
        _configuration       = configuration;
        _loggerFactory       = loggerFactory;
        _timeoutConfig       = timeoutConfig;
        _ollamaHealthMonitor = ollamaHealthMonitor;
        InitializeProviders();
    }

    private void InitializeProviders()
    {
        var anthropicKey = _configuration["SAGIDE:ApiKeys:Anthropic"] ?? "";
        var openaiKey    = _configuration["SAGIDE:ApiKeys:OpenAI"] ?? "";
        var googleKey    = _configuration["SAGIDE:ApiKeys:Google"] ?? "";

        // Per-provider token limits — configurable via SAGIDE:Providers:<Name>:MaxTokens (default 4096)
        var claudeMaxTokens = _configuration.GetValue("SAGIDE:Providers:Claude:MaxTokens", 4096);
        var geminiMaxTokens = _configuration.GetValue("SAGIDE:Providers:Gemini:MaxTokens", 4096);
        var codexMaxTokens  = _configuration.GetValue("SAGIDE:Providers:Codex:MaxTokens",  4096);
        var ollamaMaxTokens = _configuration.GetValue("SAGIDE:Providers:Ollama:MaxTokens", 4096);

        if (!string.IsNullOrEmpty(anthropicKey))
        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Claude));
            _providers[ModelProvider.Claude] = new ClaudeProvider(anthropicKey,
                BuildRetryPolicy("Claude", new RetryPolicy { RetryableStatusCodes = [429, 500, 502, 503, 529] }),
                timeout, _loggerFactory.CreateLogger<ClaudeProvider>(), claudeMaxTokens);
        }

        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Codex));
            var openAiCompatibleEndpoints = BuildOpenAICompatibleRoutingTable();
            // Only register if an API key or at least one custom endpoint is configured.
            // Without either, every call would immediately return 401/404 and land in the DLQ.
            if (!string.IsNullOrEmpty(openaiKey) || openAiCompatibleEndpoints.Count > 0)
            {
                _providers[ModelProvider.Codex] = new CodexProvider(openaiKey,
                    BuildRetryPolicy("Codex", RetryPolicy.Default),
                    timeout, _loggerFactory.CreateLogger<CodexProvider>(), openAiCompatibleEndpoints, codexMaxTokens);
            }
        }

        if (!string.IsNullOrEmpty(googleKey))
        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Gemini));
            _providers[ModelProvider.Gemini] = new GeminiProvider(googleKey,
                BuildRetryPolicy("Gemini", RetryPolicy.Default),
                timeout, _loggerFactory.CreateLogger<GeminiProvider>(), geminiMaxTokens);
        }

        // Multi-server Ollama: build model-to-endpoint routing table from config
        var ollamaTimeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Ollama));
        var modelEndpoints = BuildOllamaRoutingTable();
        var defaultServer = _configuration.GetSection("SAGIDE:Ollama:Servers")
            .GetChildren()
            .Select(s => s["BaseUrl"])
            .FirstOrDefault(u => !string.IsNullOrEmpty(u)) ?? string.Empty;

        _providers[ModelProvider.Ollama] = new OllamaProvider(
            defaultServer, modelEndpoints, BuildRetryPolicy("Ollama", RetryPolicy.ForOllama), ollamaTimeout,
            _loggerFactory.CreateLogger<OllamaProvider>(),
            _ollamaHealthMonitor, ollamaMaxTokens);
    }

    /// <summary>
    /// Reads per-provider retry policy from <c>SAGIDE:Resilience:Providers:{name}</c>.
    /// Falls back to <paramref name="defaults"/> for any field that is absent from config.
    /// </summary>
    private RetryPolicy BuildRetryPolicy(string providerName, RetryPolicy defaults)
    {
        var section = _configuration.GetSection($"SAGIDE:Resilience:Providers:{providerName}");
        if (!section.Exists()) return defaults;

        var maxRetries      = section.GetValue("MaxRetries", defaults.MaxRetries);
        var initialDelayMs  = section.GetValue("InitialDelayMs", (int)defaults.InitialDelay.TotalMilliseconds);
        var strategyStr     = section["BackoffStrategy"];
        var strategy        = strategyStr is not null && Enum.TryParse<BackoffStrategy>(strategyStr, out var s)
                                ? s : defaults.Strategy;
        var codes           = section.GetSection("RetryableStatusCodes").Get<int[]>()
                                ?? defaults.RetryableStatusCodes;

        return new RetryPolicy
        {
            MaxRetries            = maxRetries,
            InitialDelay          = TimeSpan.FromMilliseconds(initialDelayMs),
            Strategy              = strategy,
            RetryableStatusCodes  = codes,
        };
    }

    private Dictionary<string, string> BuildOllamaRoutingTable()
    {
        var table = new Dictionary<string, string>();
        var serversSection = _configuration.GetSection("SAGIDE:Ollama:Servers");
        foreach (var server in serversSection.GetChildren())
        {
            var baseUrl = server["BaseUrl"] ?? "";
            var modelsSection = server.GetSection("Models");
            foreach (var modelEntry in modelsSection.GetChildren())
            {
                var modelId = modelEntry.Value ?? "";
                if (!string.IsNullOrEmpty(modelId) && !string.IsNullOrEmpty(baseUrl))
                    table[modelId] = baseUrl;
            }
        }
        return table;
    }

    private Dictionary<string, string> BuildOpenAICompatibleRoutingTable()
    {
        var table = new Dictionary<string, string>();
        var serversSection = _configuration.GetSection("SAGIDE:OpenAICompatible:Servers");
        foreach (var server in serversSection.GetChildren())
        {
            var baseUrl = server["BaseUrl"] ?? "";
            var modelsSection = server.GetSection("Models");
            foreach (var modelEntry in modelsSection.GetChildren())
            {
                var modelId = modelEntry.Value ?? "";
                if (!string.IsNullOrEmpty(modelId) && !string.IsNullOrEmpty(baseUrl))
                    table[modelId] = baseUrl;
            }
        }
        return table;
    }

    /// <summary>
    /// Wires routing hints into the Ollama provider after the DI container is built.
    /// Called from Program.cs after builder.Build() — provider is eagerly created,
    /// hints are DI-resolved singletons.
    /// </summary>
    public void SetRoutingHints(ModelRoutingHints? hints)
    {
        if (_providers.TryGetValue(ModelProvider.Ollama, out var p) && p is OllamaProvider ollama)
            ollama.SetRoutingHints(hints);
    }

    public IAgentProvider? GetProvider(ModelProvider provider)
    {
        _providers.TryGetValue(provider, out var agentProvider);
        return agentProvider;
    }

    public IEnumerable<IAgentProvider> GetAllProviders() => _providers.Values;
    public IReadOnlyList<ModelProvider> GetAvailableProviders() => _providers.Keys.ToList();
}
