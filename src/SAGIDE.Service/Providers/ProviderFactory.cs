using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

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
                new RetryPolicy { RetryableStatusCodes = [429, 500, 502, 503, 529] },
                timeout, _loggerFactory.CreateLogger<ClaudeProvider>(), claudeMaxTokens);
        }

        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Codex));
            var openAiCompatibleEndpoints = BuildOpenAICompatibleRoutingTable();
            // Only register if an API key or at least one custom endpoint is configured.
            // Without either, every call would immediately return 401/404 and land in the DLQ.
            if (!string.IsNullOrEmpty(openaiKey) || openAiCompatibleEndpoints.Count > 0)
            {
                _providers[ModelProvider.Codex] = new CodexProvider(openaiKey, RetryPolicy.Default,
                    timeout, _loggerFactory.CreateLogger<CodexProvider>(), openAiCompatibleEndpoints, codexMaxTokens);
            }
        }

        if (!string.IsNullOrEmpty(googleKey))
        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Gemini));
            _providers[ModelProvider.Gemini] = new GeminiProvider(googleKey, RetryPolicy.Default,
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
            defaultServer, modelEndpoints, RetryPolicy.ForOllama, ollamaTimeout,
            _loggerFactory.CreateLogger<OllamaProvider>(),
            _ollamaHealthMonitor, ollamaMaxTokens);
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

    public IAgentProvider? GetProvider(ModelProvider provider)
    {
        _providers.TryGetValue(provider, out var agentProvider);
        return agentProvider;
    }

    public IEnumerable<IAgentProvider> GetAllProviders() => _providers.Values;
    public IReadOnlyList<ModelProvider> GetAvailableProviders() => _providers.Keys.ToList();
}
