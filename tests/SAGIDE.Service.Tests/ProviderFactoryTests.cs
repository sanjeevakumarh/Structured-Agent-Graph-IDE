using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Providers;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for ProviderFactory — verifies that providers are registered
/// only when the necessary configuration (API keys / endpoints) is present.
/// Uses Microsoft.Extensions.Configuration.Memory so no external dependencies.
/// </summary>
public class ProviderFactoryTests
{
    // ── Ollama — always registered (local, no key required) ──────────────────

    [Fact]
    public void NoConfig_OllamaAlwaysRegistered()
    {
        var factory = Build([]);
        var providers = factory.GetAvailableProviders();

        Assert.Contains(ModelProvider.Ollama, providers);
    }

    [Fact]
    public void NoConfig_CloudProvidersNotRegistered()
    {
        var factory = Build([]);
        var providers = factory.GetAvailableProviders();

        Assert.DoesNotContain(ModelProvider.Claude, providers);
        Assert.DoesNotContain(ModelProvider.Gemini, providers);
    }

    // ── Codex — gated on key or custom endpoints ──────────────────────────────

    [Fact]
    public void NoKeyNoEndpoints_CodexNotRegistered()
    {
        var factory = Build([]);
        Assert.DoesNotContain(ModelProvider.Codex, factory.GetAvailableProviders());
    }

    [Fact]
    public void OpenAIKeyPresent_CodexRegistered()
    {
        var factory = Build([
            new("SAGIDE:ApiKeys:OpenAI", "sk-test-openai"),
        ]);
        Assert.Contains(ModelProvider.Codex, factory.GetAvailableProviders());
    }

    [Fact]
    public void OpenAICompatibleEndpoint_NoKey_CodexRegistered()
    {
        var factory = Build([
            new("SAGIDE:OpenAICompatible:Servers:0:BaseUrl", "http://my-custom-llm:8080"),
            new("SAGIDE:OpenAICompatible:Servers:0:Models:0", "custom-model"),
        ]);
        Assert.Contains(ModelProvider.Codex, factory.GetAvailableProviders());
    }

    // ── Claude — gated on Anthropic key ──────────────────────────────────────

    [Fact]
    public void AnthropicKeyPresent_ClaudeRegistered()
    {
        var factory = Build([
            new("SAGIDE:ApiKeys:Anthropic", "sk-ant-test"),
        ]);
        Assert.Contains(ModelProvider.Claude, factory.GetAvailableProviders());
    }

    [Fact]
    public void NoAnthropicKey_ClaudeNotRegistered()
    {
        var factory = Build([]);
        Assert.DoesNotContain(ModelProvider.Claude, factory.GetAvailableProviders());
    }

    // ── Gemini — gated on Google key ─────────────────────────────────────────

    [Fact]
    public void GoogleKeyPresent_GeminiRegistered()
    {
        var factory = Build([
            new("SAGIDE:ApiKeys:Google", "google-key-test"),
        ]);
        Assert.Contains(ModelProvider.Gemini, factory.GetAvailableProviders());
    }

    [Fact]
    public void NoGoogleKey_GeminiNotRegistered()
    {
        var factory = Build([]);
        Assert.DoesNotContain(ModelProvider.Gemini, factory.GetAvailableProviders());
    }

    // ── All providers ─────────────────────────────────────────────────────────

    [Fact]
    public void AllKeysPresent_AllProvidersRegistered()
    {
        var factory = Build([
            new("SAGIDE:ApiKeys:Anthropic", "sk-ant-test"),
            new("SAGIDE:ApiKeys:OpenAI",    "sk-openai-test"),
            new("SAGIDE:ApiKeys:Google",    "google-key-test"),
        ]);

        var providers = factory.GetAvailableProviders();
        Assert.Contains(ModelProvider.Claude, providers);
        Assert.Contains(ModelProvider.Codex,  providers);
        Assert.Contains(ModelProvider.Gemini, providers);
        Assert.Contains(ModelProvider.Ollama, providers);
    }

    // ── GetProvider ───────────────────────────────────────────────────────────

    [Fact]
    public void GetProvider_Registered_ReturnsInstance()
    {
        var factory = Build([]);
        var provider = factory.GetProvider(ModelProvider.Ollama);
        Assert.NotNull(provider);
        Assert.Equal(ModelProvider.Ollama, provider.Provider);
    }

    [Fact]
    public void GetProvider_NotRegistered_ReturnsNull()
    {
        var factory = Build([]);
        var provider = factory.GetProvider(ModelProvider.Claude);
        Assert.Null(provider);
    }

    // ── GetAllProviders ───────────────────────────────────────────────────────

    [Fact]
    public void GetAllProviders_CountMatchesRegistered()
    {
        var factory = Build([
            new("SAGIDE:ApiKeys:Anthropic", "key"),
        ]);
        var all = factory.GetAllProviders().ToList();
        // Ollama + Claude = 2
        Assert.Equal(2, all.Count);
    }

    // ── Ollama routing table built from config ────────────────────────────────

    [Fact]
    public void OllamaDefaultServer_ConfigOverridesDefault()
    {
        var factory = Build([
            new("SAGIDE:Ollama:DefaultServer", "http://gpu-server:11434"),
        ]);
        // Provider should be registered (always) — we can't inspect the URL directly
        // but confirm it doesn't throw during construction
        Assert.Contains(ModelProvider.Ollama, factory.GetAvailableProviders());
    }

    // ── Token limits ──────────────────────────────────────────────────────────

    [Fact]
    public void OllamaMaxTokensConfig_DoesNotThrow()
    {
        // Custom token limit should be wired without error
        var factory = Build([
            new("SAGIDE:Providers:Ollama:MaxTokens", "8192"),
        ]);
        Assert.Contains(ModelProvider.Ollama, factory.GetAvailableProviders());
    }

    // ── RetryPolicy.ForOllama wired ───────────────────────────────────────────

    [Fact]
    public void ForOllamaPolicy_HasDifferentDefaultsThanGlobal()
    {
        // This guards the intentional distinction between ForOllama and Default
        Assert.NotEqual(RetryPolicy.Default.MaxRetries,    RetryPolicy.ForOllama.MaxRetries);
        Assert.NotEqual(RetryPolicy.Default.InitialDelay,  RetryPolicy.ForOllama.InitialDelay);
        Assert.NotEqual(RetryPolicy.Default.Strategy,      RetryPolicy.ForOllama.Strategy);
    }

    // ── Helper ────────────────────────────────────────────────────────────────

    private static ProviderFactory Build(IEnumerable<KeyValuePair<string, string?>> pairs)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(pairs)
            .Build();

        var timeoutConfig = new TimeoutConfig();
        var loggerFactory = NullLoggerFactory.Instance;

        return new ProviderFactory(config, loggerFactory, timeoutConfig, ollamaHealthMonitor: null);
    }
}
