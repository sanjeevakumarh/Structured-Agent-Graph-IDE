using SAGIDE.Core.Models;
using SAGIDE.Service.Scheduling;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for the internal helper methods in <see cref="SchedulerService"/>.
/// Full tick-behaviour tests would require extracting <c>AgentOrchestrator</c> and
/// <c>SubtaskCoordinator</c> behind interfaces; this set covers the parsing/routing logic.
/// </summary>
public class SchedulerServiceTests
{
    // ── ParseProviderFromModelId ───────────────────────────────────────────────

    [Theory]
    [InlineData("claude-3-5-sonnet",       ModelProvider.Claude)]
    [InlineData("claude-opus-4",           ModelProvider.Claude)]
    [InlineData("CLAUDE-HAIKU",            ModelProvider.Claude)]
    public void ParseProvider_ClaudeVariants_ReturnsClaude(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, SchedulerService.ParseProviderFromModelId(modelId));
    }

    [Theory]
    [InlineData("ollama/llama3",           ModelProvider.Ollama)]
    [InlineData("ollama/deepseek-r1:14b",  ModelProvider.Ollama)]
    [InlineData("OLLAMA/mistral",          ModelProvider.Ollama)]
    public void ParseProvider_OllamaVariants_ReturnsOllama(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, SchedulerService.ParseProviderFromModelId(modelId));
    }

    [Theory]
    [InlineData("codex/gpt-4o",            ModelProvider.Codex)]
    [InlineData("openai/gpt-4",            ModelProvider.Codex)]
    [InlineData("CODEX/davinci",           ModelProvider.Codex)]
    [InlineData("OPENAI/o1-preview",       ModelProvider.Codex)]
    public void ParseProvider_CodexVariants_ReturnsCodex(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, SchedulerService.ParseProviderFromModelId(modelId));
    }

    [Theory]
    [InlineData("gemini/gemini-pro",       ModelProvider.Gemini)]
    [InlineData("GEMINI/1.5-flash",        ModelProvider.Gemini)]
    public void ParseProvider_GeminiVariants_ReturnsGemini(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, SchedulerService.ParseProviderFromModelId(modelId));
    }

    [Theory]
    [InlineData("unknown-model",           ModelProvider.Ollama)]
    [InlineData("",                        ModelProvider.Ollama)]
    [InlineData("llama3",                  ModelProvider.Ollama)]
    [InlineData("some-random-model-name",  ModelProvider.Ollama)]
    public void ParseProvider_Unknown_DefaultsToOllama(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, SchedulerService.ParseProviderFromModelId(modelId));
    }

    // ── StripProviderPrefix ───────────────────────────────────────────────────

    [Fact]
    public void StripPrefix_WithSlash_ReturnsPartAfterSlash()
    {
        Assert.Equal("llama3",          SchedulerService.StripProviderPrefix("ollama/llama3"));
        Assert.Equal("gpt-4o",          SchedulerService.StripProviderPrefix("openai/gpt-4o"));
        Assert.Equal("deepseek-r1:14b", SchedulerService.StripProviderPrefix("ollama/deepseek-r1:14b"));
    }

    [Fact]
    public void StripPrefix_NoSlash_ReturnsOriginalString()
    {
        Assert.Equal("claude-3-sonnet", SchedulerService.StripProviderPrefix("claude-3-sonnet"));
        Assert.Equal("llama3",          SchedulerService.StripProviderPrefix("llama3"));
        Assert.Equal("",                SchedulerService.StripProviderPrefix(""));
    }

    [Fact]
    public void StripPrefix_MultipleSlashes_ReturnsEverythingAfterFirst()
    {
        // Only the first slash is treated as the separator
        Assert.Equal("sub/path", SchedulerService.StripProviderPrefix("prefix/sub/path"));
    }
}
