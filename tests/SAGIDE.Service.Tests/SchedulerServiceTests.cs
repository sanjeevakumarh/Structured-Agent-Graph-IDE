using SAGIDE.Core.Models;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for <see cref="ModelIdParser"/> — shared model ID parsing/routing logic.
/// </summary>
public class ModelIdParserTests
{
    // ── ParseProvider ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("claude-3-5-sonnet",       ModelProvider.Claude)]
    [InlineData("claude-opus-4",           ModelProvider.Claude)]
    [InlineData("CLAUDE-HAIKU",            ModelProvider.Claude)]
    public void ParseProvider_ClaudeVariants_ReturnsClaude(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, ModelIdParser.ParseProvider(modelId));
    }

    [Theory]
    [InlineData("ollama/llama3",           ModelProvider.Ollama)]
    [InlineData("ollama/deepseek-r1:14b",  ModelProvider.Ollama)]
    [InlineData("OLLAMA/mistral",          ModelProvider.Ollama)]
    public void ParseProvider_OllamaVariants_ReturnsOllama(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, ModelIdParser.ParseProvider(modelId));
    }

    [Theory]
    [InlineData("codex/gpt-4o",            ModelProvider.Codex)]
    [InlineData("openai/gpt-4",            ModelProvider.Codex)]
    [InlineData("CODEX/davinci",           ModelProvider.Codex)]
    [InlineData("OPENAI/o1-preview",       ModelProvider.Codex)]
    public void ParseProvider_CodexVariants_ReturnsCodex(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, ModelIdParser.ParseProvider(modelId));
    }

    [Theory]
    [InlineData("gemini/gemini-pro",       ModelProvider.Gemini)]
    [InlineData("GEMINI/1.5-flash",        ModelProvider.Gemini)]
    public void ParseProvider_GeminiVariants_ReturnsGemini(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, ModelIdParser.ParseProvider(modelId));
    }

    [Theory]
    [InlineData("unknown-model",           ModelProvider.Ollama)]
    [InlineData("",                        ModelProvider.Ollama)]
    [InlineData("llama3",                  ModelProvider.Ollama)]
    [InlineData("some-random-model-name",  ModelProvider.Ollama)]
    public void ParseProvider_Unknown_DefaultsToOllama(string modelId, ModelProvider expected)
    {
        Assert.Equal(expected, ModelIdParser.ParseProvider(modelId));
    }

    // ── StripProviderPrefix ───────────────────────────────────────────────────

    [Fact]
    public void StripPrefix_WithSlash_ReturnsPartAfterSlash()
    {
        Assert.Equal("llama3",          ModelIdParser.StripPrefix("ollama/llama3"));
        Assert.Equal("gpt-4o",          ModelIdParser.StripPrefix("openai/gpt-4o"));
        Assert.Equal("deepseek-r1:14b", ModelIdParser.StripPrefix("ollama/deepseek-r1:14b"));
    }

    [Fact]
    public void StripPrefix_NoSlash_ReturnsOriginalString()
    {
        Assert.Equal("claude-3-sonnet", ModelIdParser.StripPrefix("claude-3-sonnet"));
        Assert.Equal("llama3",          ModelIdParser.StripPrefix("llama3"));
        Assert.Equal("",                ModelIdParser.StripPrefix(""));
    }

    [Fact]
    public void StripPrefix_MultipleSlashes_ReturnsEverythingAfterFirst()
    {
        // Only the first slash is treated as the separator
        Assert.Equal("sub/path", ModelIdParser.StripPrefix("prefix/sub/path"));
    }
}
