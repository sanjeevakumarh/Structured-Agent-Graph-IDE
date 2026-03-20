using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

public class CodexProvider : IAgentProvider
{
    private readonly ILogger<CodexProvider> _logger;
    private readonly string _apiKey;
    private readonly RetryPolicy _retryPolicy;
    private readonly TimeSpan _timeout;
    private readonly Dictionary<string, string> _modelEndpoints; // modelId → base URL (no trailing slash)
    private readonly ConcurrentDictionary<string, (HttpClient Client, ResilientHttpHandler Handler)> _clientsByUrl = new();

    private const string OpenAiBaseUrl = "https://api.openai.com";

    public ModelProvider Provider => ModelProvider.Codex;
    public int LastInputTokens { get; private set; }
    public int LastOutputTokens { get; private set; }

    private readonly int _maxTokens;

    public CodexProvider(
        string apiKey,
        RetryPolicy retryPolicy,
        TimeSpan timeout,
        ILogger<CodexProvider> logger,
        Dictionary<string, string>? modelEndpoints = null,
        int maxTokens = 4096)
    {
        _apiKey = apiKey;
        _logger = logger;
        _retryPolicy = retryPolicy;
        _timeout = timeout;
        _modelEndpoints = modelEndpoints ?? [];
        _maxTokens = maxTokens;
    }

    private (HttpClient Client, ResilientHttpHandler Handler) GetOrCreateClientPair(string baseUrl)
    {
        return _clientsByUrl.GetOrAdd(baseUrl, url =>
        {
            var client = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            if (url.Contains("api.openai.com") && !string.IsNullOrEmpty(_apiKey))
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
            var handler = new ResilientHttpHandler(client, _retryPolicy, _timeout, _logger);
            return (client, handler);
        });
    }

    private string ResolveBaseUrl(ModelConfig model)
    {
        if (!string.IsNullOrEmpty(model.Endpoint))
            return model.Endpoint.TrimEnd('/');
        if (_modelEndpoints.TryGetValue(model.ModelId, out var url))
            return url.TrimEnd('/');
        return OpenAiBaseUrl;
    }

    public async Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(model);
        var (_, handler) = GetOrCreateClientPair(baseUrl);
        var completionsUrl = $"{baseUrl}/v1/chat/completions";

        var requestBody = new
        {
            model = model.ModelId,
            max_tokens = _maxTokens,
            temperature = 0,
            messages = new[] { new { role = "user", content = prompt } }
        };
        var json = JsonSerializer.Serialize(requestBody);

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, completionsUrl);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        _logger.LogDebug("Calling OpenAI-compatible API at {Url} with model {Model}", completionsUrl, model.ModelId);

        var response = await handler.SendWithRetryAsync(CreateRequest, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(responseJson);
        string? textContent = null;
        if (doc.RootElement.TryGetProperty("choices", out var choicesArr) &&
            choicesArr.GetArrayLength() > 0 &&
            choicesArr[0].TryGetProperty("message", out var msg))
        {
            if (msg.TryGetProperty("content", out var cnt))
                textContent = cnt.GetString();

            // Fallback to reasoning field for reasoning models (e.g. gpt-oss)
            if (string.IsNullOrEmpty(textContent) &&
                msg.TryGetProperty("reasoning", out var reasoning))
            {
                textContent = reasoning.GetString();
                _logger.LogWarning("Model {Model} returned reasoning instead of content — using reasoning as output",
                    model.ModelId);
            }
        }

        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            LastInputTokens = usage.TryGetProperty("prompt_tokens", out var inp) ? inp.GetInt32() : 0;
            LastOutputTokens = usage.TryGetProperty("completion_tokens", out var outp) ? outp.GetInt32() : 0;

            _logger.LogInformation(
                "OpenAI-compatible API: {InputTokens} input + {OutputTokens} output tokens ({Attempts} attempt(s))",
                LastInputTokens, LastOutputTokens, handler.TotalAttempts);
        }
        else
        {
            _logger.LogDebug("OpenAI-compatible API responded from {BaseUrl} ({Attempts} attempt(s))",
                baseUrl, handler.TotalAttempts);
        }

        return textContent ?? string.Empty;
    }

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var baseUrl = ResolveBaseUrl(model);
        var (client, _) = GetOrCreateClientPair(baseUrl);
        var completionsUrl = $"{baseUrl}/v1/chat/completions";

        var requestBody = new
        {
            model = model.ModelId,
            max_tokens = _maxTokens,
            temperature = 0,
            stream = true,
            messages = new[] { new { role = "user", content = prompt } }
        };
        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, completionsUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        var hasContent = false;
        var reasoningBuffer = new StringBuilder();
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;
            string? contentText = null;
            string? reasoningText = null;
            try
            {
                var doc = JsonDocument.Parse(data);
                if (doc.RootElement.TryGetProperty("choices", out var choices) &&
                    choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta))
                {
                    if (delta.TryGetProperty("content", out var content))
                        contentText = content.GetString();
                    if (delta.TryGetProperty("reasoning", out var reasoning))
                        reasoningText = reasoning.GetString();
                }
            }
            catch (JsonException) { continue; }

            // Prefer content tokens (actual output)
            if (!string.IsNullOrEmpty(contentText))
            {
                hasContent = true;
                yield return contentText;
            }
            // Buffer reasoning tokens as fallback for reasoning models (e.g. gpt-oss)
            else if (!hasContent && !string.IsNullOrEmpty(reasoningText))
            {
                reasoningBuffer.Append(reasoningText);
            }
        }

        // If model produced only reasoning (no content), yield the reasoning as output
        if (!hasContent && reasoningBuffer.Length > 0)
        {
            _logger.LogWarning(
                "Model {Model} produced {ReasoningChars} reasoning chars but no content — using reasoning as output",
                model.ModelId, reasoningBuffer.Length);
            yield return reasoningBuffer.ToString();
        }

        _logger.LogInformation("OpenAI-compatible streaming complete (model {Model}, endpoint {BaseUrl})",
            model.ModelId, baseUrl);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return Task.FromResult(!string.IsNullOrEmpty(_apiKey) || _modelEndpoints.Count > 0);
    }
}
