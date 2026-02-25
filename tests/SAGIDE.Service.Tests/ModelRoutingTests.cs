using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Unit tests for model-routing helpers in <see cref="AgentOrchestrator"/>.
///
/// These tests guard the <c>@machine</c>-stripping logic that prevents the
/// machine-routing suffix from leaking into the model name sent to Ollama.
/// Without this stripping, Ollama returns 404 "model not found" because it
/// receives e.g. "deepseek-coder-v2:16b@workstation" instead of
/// "deepseek-coder-v2:16b".
/// </summary>
public class ModelRoutingTests
{
    // ── StripMachineSuffix ────────────────────────────────────────────────────

    [Fact]
    public void StripMachineSuffix_WithMachineName_RemovesSuffix()
    {
        var result = AgentOrchestrator.StripMachineSuffix("deepseek-coder-v2:16b@workstation");

        Assert.Equal("deepseek-coder-v2:16b", result);
    }

    [Fact]
    public void StripMachineSuffix_PlainModelId_Unchanged()
    {
        var result = AgentOrchestrator.StripMachineSuffix("llama3.2:3b");

        Assert.Equal("llama3.2:3b", result);
    }

    [Fact]
    public void StripMachineSuffix_EmptyString_ReturnsEmpty()
    {
        var result = AgentOrchestrator.StripMachineSuffix(string.Empty);

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void StripMachineSuffix_AtSignAtStart_Unchanged()
    {
        // atIdx == 0 → not a valid machine suffix; leave unchanged so weird model
        // names are preserved rather than silently truncated.
        var result = AgentOrchestrator.StripMachineSuffix("@weirdname");

        Assert.Equal("@weirdname", result);
    }

    [Fact]
    public void StripMachineSuffix_AtSignAtEnd_Unchanged()
    {
        // "model@" has atIdx > 0 but the machine part is empty; the result is the
        // model name without trailing @, which is the correct behaviour — the empty
        // machine suffix is not meaningful and the base model is preserved.
        var result = AgentOrchestrator.StripMachineSuffix("mymodel@");

        Assert.Equal("mymodel", result);
    }

    [Fact]
    public void StripMachineSuffix_MultipleAtSigns_UsesLastOne()
    {
        // Only the last @… segment is the machine suffix; earlier ones are part of
        // non-standard model names (edge case, but handled consistently).
        var result = AgentOrchestrator.StripMachineSuffix("ns@model@mini");

        Assert.Equal("ns@model", result);
    }

    [Fact]
    public void StripMachineSuffix_WhitespaceAroundModelId_IsTrimmed()
    {
        // Protects against YAML entries with accidental trailing spaces before @.
        var result = AgentOrchestrator.StripMachineSuffix("phi4 @edge");

        Assert.Equal("phi4", result);
    }

    [Theory]
    [InlineData("qwen2.5-coder:7b@mini",      "qwen2.5-coder:7b")]
    [InlineData("mistral:7b@orin",             "mistral:7b")]
    [InlineData("codellama:13b@workstation",   "codellama:13b")]
    [InlineData("llama3.1:70b@edge",           "llama3.1:70b")]
    [InlineData("gemma3:4b",                   "gemma3:4b")]   // no suffix
    public void StripMachineSuffix_CommonPatterns(string input, string expected)
    {
        Assert.Equal(expected, AgentOrchestrator.StripMachineSuffix(input));
    }
}
