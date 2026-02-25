using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

public class GeminiProvider : BaseHttpAgentProvider
{
    private readonly string _apiKey; // kept to build model-specific URL at request time

    public override ModelProvider Provider => ModelProvider.Gemini;

    public GeminiProvider(
        string apiKey,
        RetryPolicy retryPolicy,
        TimeSpan timeout,
        ILogger<GeminiProvider> logger,
        int maxTokens = 4096)
        : base(
            "https://generativelanguage.googleapis.com/",
            new Dictionary<string, string>(), // Gemini auth is via URL query param, not header
            retryPolicy, timeout, logger,
            isConfigured: !string.IsNullOrEmpty(apiKey),
            maxTokens: maxTokens)
    {
        _apiKey = apiKey;
    }

    protected override string GetCompletionEndpoint(ModelConfig model)
        => $"v1beta/models/{model.ModelId}:generateContent?key={_apiKey}";

    // Gemini streaming uses a different path and requires ?alt=sse
    protected override string GetStreamingEndpoint(ModelConfig model)
        => $"v1beta/models/{model.ModelId}:streamGenerateContent?alt=sse&key={_apiKey}";

    protected override object BuildRequestBody(string prompt, ModelConfig model) => new
    {
        contents = new[] { new { parts = new[] { new { text = prompt } } } },
        // Determinism: temperature=0 for consistent outputs
        generationConfig = new { maxOutputTokens = _maxTokens, temperature = 0.0 }
    };

    protected override object BuildStreamingRequestBody(string prompt, ModelConfig model)
        => BuildRequestBody(prompt, model); // Gemini uses the same body for streaming

    protected override string? ExtractContent(JsonDocument response)
        => response.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

    protected override void ExtractTokenUsage(JsonDocument response)
    {
        if (response.RootElement.TryGetProperty("usageMetadata", out var usage))
        {
            LastInputTokens  = usage.TryGetProperty("promptTokenCount",     out var inp)  ? inp.GetInt32()  : 0;
            LastOutputTokens = usage.TryGetProperty("candidatesTokenCount", out var outp) ? outp.GetInt32() : 0;
        }
    }

    // Gemini SSE: each chunk is a full candidate object
    protected override string? ExtractStreamDelta(JsonDocument doc)
    {
        var candidates = doc.RootElement.GetProperty("candidates");
        if (candidates.GetArrayLength() > 0)
            return candidates[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();
        return null;
    }

    // Gemini stream ends on EOF (no [DONE] sentinel); keep default IsStreamDone → false
}
