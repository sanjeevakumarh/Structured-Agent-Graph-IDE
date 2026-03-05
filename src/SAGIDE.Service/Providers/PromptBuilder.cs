using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Providers;

/// <summary>
/// Assembles the final prompt string sent to the LLM for a given AgentTask.
/// Extracted from AgentOrchestrator.BuildPrompt: reads file contents, applies per-file
/// truncation, and prefixes the agent role description based on AgentType.
/// </summary>
public sealed class PromptBuilder
{
    private readonly int _maxFileSizeChars;
    private readonly ILogger<PromptBuilder> _logger;

    public PromptBuilder(
        int maxFileSizeChars = 32_000,
        ILogger<PromptBuilder>? logger = null)
    {
        _maxFileSizeChars = maxFileSizeChars;
        _logger           = logger ?? Microsoft.Extensions.Logging.Abstractions
                                .NullLogger<PromptBuilder>.Instance;
    }

    public async Task<string> BuildAsync(AgentTask task)
    {
        // Read actual file contents for the prompt.
        // Cache by absolute path so duplicate entries in FilePaths are read only once.
        var fileContents = new List<string>();
        var fileCache    = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in task.FilePaths)
        {
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("PromptBuilder: file not found — '{Path}'", filePath);
                continue;
            }

            if (!fileCache.TryGetValue(filePath, out var content))
            {
                content = await File.ReadAllTextAsync(filePath);
                if (content.Length > _maxFileSizeChars)
                {
                    content = string.Concat(content.AsSpan(0, _maxFileSizeChars),
                        $"\n... [file truncated to {_maxFileSizeChars:N0} chars]");
                }
                fileCache[filePath] = content;
            }
            fileContents.Add($"### {Path.GetFileName(filePath)}\n```\n{content}\n```");
        }

        var fileContext = fileContents.Count > 0
            ? $"\n\n## Files\n{string.Join("\n\n", fileContents)}"
            : "";

        var jsonInstructions = "\n\nReturn your findings in a ```json block with an \"issues\" array " +
            "(each with filePath, line, severity, message, suggestedFix) and optionally a \"changes\" " +
            "array (each with filePath, original, newContent, description).";

        return task.AgentType switch
        {
            AgentType.CodeReview =>
                $"You are a code review agent. Review the following code for security vulnerabilities, " +
                $"performance issues, and best practices violations. Provide specific line numbers and " +
                $"actionable suggestions.{jsonInstructions}\n\n{task.Description}{fileContext}",
            AgentType.TestGeneration =>
                $"You are a test generation agent. Generate comprehensive unit tests for the given code. " +
                $"Cover edge cases and error conditions. Return the test code in a ```csharp block." +
                $"{jsonInstructions}\n\n{task.Description}{fileContext}",
            AgentType.Refactoring =>
                $"You are a refactoring agent. Improve code quality by reducing complexity, improving " +
                $"readability, and applying design patterns where appropriate.{jsonInstructions}\n\n" +
                $"{task.Description}{fileContext}",
            AgentType.Debug =>
                $"You are a debugging agent. Analyze the error and identify the root cause. " +
                $"Provide a fix with explanation.{jsonInstructions}\n\n{task.Description}{fileContext}",
            AgentType.Documentation =>
                $"You are a documentation agent. Generate or update code documentation including " +
                $"XML docs, JSDoc, or docstrings as appropriate.{jsonInstructions}\n\n" +
                $"{task.Description}{fileContext}",
            AgentType.SecurityReview =>
                $"You are a security review agent. Analyze the code for OWASP Top 10 vulnerabilities, " +
                $"injection flaws, authentication weaknesses, insecure deserialization, and sensitive " +
                $"data exposure. Reference relevant CVEs where applicable.{jsonInstructions}\n\n" +
                $"{task.Description}{fileContext}",
            _ => task.Description ?? string.Empty,
        };
    }
}
