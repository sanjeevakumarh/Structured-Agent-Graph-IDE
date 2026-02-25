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
