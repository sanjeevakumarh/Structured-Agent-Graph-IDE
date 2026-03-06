namespace SAGIDE.Core.Models;

public enum ModelProvider { Claude, Codex, Gemini, Ollama }

public record ModelConfig(ModelProvider Provider, string ModelId, string? ApiKey = null, string? Endpoint = null)
{
    public static ModelConfig Claude(string modelId, string? apiKey = null)
        => new(ModelProvider.Claude, modelId, apiKey);

    public static ModelConfig Codex(string modelId, string? apiKey = null)
        => new(ModelProvider.Codex, modelId, apiKey);

    public static ModelConfig Gemini(string modelId, string? apiKey = null)
        => new(ModelProvider.Gemini, modelId, apiKey);

    // Model ID and endpoint both come from SAGIDE:Ollama:Servers in appsettings.json
    public static ModelConfig Local(string modelId, string endpoint)
        => new(ModelProvider.Ollama, modelId, Endpoint: endpoint);
}

/// <summary>
/// Shared helpers for parsing provider/model ID from a combined model spec string.
/// Used by PromptEndpoints, SchedulerService, and anywhere a raw model string
/// (e.g. "ollama/llama3:8b", "claude-sonnet-4-6") needs to be split into provider + clean ID.
/// </summary>
public static class ModelIdParser
{
    /// <summary>
    /// Infers <see cref="ModelProvider"/> from a model ID string prefix.
    /// Defaults to <see cref="ModelProvider.Ollama"/> when no known prefix is found.
    /// </summary>
    public static ModelProvider ParseProvider(string modelId)
    {
        if (modelId.StartsWith("claude", StringComparison.OrdinalIgnoreCase))  return ModelProvider.Claude;
        if (modelId.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase)) return ModelProvider.Ollama;
        if (modelId.StartsWith("codex/", StringComparison.OrdinalIgnoreCase) ||
            modelId.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)) return ModelProvider.Codex;
        if (modelId.StartsWith("gemini/", StringComparison.OrdinalIgnoreCase)) return ModelProvider.Gemini;
        return ModelProvider.Ollama;
    }

    /// <summary>
    /// Strips the "provider/" prefix from a model ID, returning just the model name.
    /// E.g. "ollama/llama3:8b" → "llama3:8b", "claude-sonnet-4-6" → "claude-sonnet-4-6".
    /// </summary>
    public static string StripPrefix(string modelId)
    {
        var slash = modelId.IndexOf('/');
        return slash >= 0 ? modelId[(slash + 1)..] : modelId;
    }
}
