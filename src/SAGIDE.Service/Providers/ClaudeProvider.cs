using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

public class ClaudeProvider : BaseHttpAgentProvider
{
    public override ModelProvider Provider => ModelProvider.Claude;

    public ClaudeProvider(
        string apiKey,
        RetryPolicy retryPolicy,
        TimeSpan timeout,
        ILogger<ClaudeProvider> logger,
        int maxTokens = 4096)
        : base(
            "https://api.anthropic.com/",
            new Dictionary<string, string>
            {
                ["x-api-key"]          = apiKey,
                ["anthropic-version"]  = "2023-06-01",
            },
            retryPolicy, timeout, logger,
            isConfigured: !string.IsNullOrEmpty(apiKey),
            maxTokens: maxTokens)
    { }

    protected override string GetCompletionEndpoint(ModelConfig model) => "v1/messages";

    protected override object BuildRequestBody(string prompt, ModelConfig model) => new
    {
        model       = model.ModelId,
        max_tokens  = _maxTokens,
        temperature = 0,          // Determinism
        messages    = new[] { new { role = "user", content = prompt } }
    };

    protected override object BuildStreamingRequestBody(string prompt, ModelConfig model) => new
    {
        model       = model.ModelId,
        max_tokens  = _maxTokens,
        temperature = 0,
        stream      = true,
        messages    = new[] { new { role = "user", content = prompt } }
    };

    protected override string? ExtractContent(JsonDocument response)
    {
        // Use TryGetProperty to avoid KeyNotFoundException when the API response
        // has an unexpected shape (e.g. error responses, future API changes).
        if (response.RootElement.TryGetProperty("content", out var content) &&
            content.ValueKind == JsonValueKind.Array &&
            content.GetArrayLength() > 0 &&
            content[0].TryGetProperty("text", out var text))
        {
            return text.GetString();
        }

        _logger.LogWarning("Claude API response missing expected 'content[0].text' field");
        return null;
    }

    protected override void ExtractTokenUsage(JsonDocument response)
    {
        if (response.RootElement.TryGetProperty("usage", out var usage))
        {
            LastInputTokens  = usage.TryGetProperty("input_tokens",  out var inp)  ? inp.GetInt32()  : 0;
            LastOutputTokens = usage.TryGetProperty("output_tokens", out var outp) ? outp.GetInt32() : 0;
        }
    }

    // Claude SSE: each content_block_delta carries { delta: { type: "text_delta", text: "..." } }
    protected override string? ExtractStreamDelta(JsonDocument doc)
    {
        if (doc.RootElement.TryGetProperty("delta", out var delta) &&
            delta.TryGetProperty("text", out var text))
            return text.GetString();
        return null;
    }

    // Claude signals end-of-stream with the literal "data: [DONE]" line
    protected override bool IsStreamDone(string dataLine) => dataLine == "[DONE]";
}
