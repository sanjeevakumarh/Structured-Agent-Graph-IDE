using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Agents;

/// <summary>
/// Parses raw LLM text output into structured AgentResult with Issues and FileChanges.
/// Supports both JSON-block output and markdown-formatted output.
/// </summary>
public partial class ResultParser
{
    private readonly ILogger<ResultParser> _logger;

    public ResultParser(ILogger<ResultParser> logger)
    {
        _logger = logger;
    }

    public AgentResult Parse(string taskId, AgentType agentType, string rawOutput, long latencyMs)
    {
        var result = new AgentResult
        {
            TaskId = taskId,
            Success = true,
            Output = rawOutput,
            LatencyMs = latencyMs
        };

        try
        {
            // Try JSON block extraction first (```json ... ```)
            var jsonBlock = ExtractJsonBlock(rawOutput);
            if (jsonBlock is not null)
            {
                ParseJsonBlock(result, agentType, jsonBlock);
                return result;
            }

            // Fall back to markdown parsing
            switch (agentType)
            {
                case AgentType.CodeReview:
                    result.Issues = ParseCodeReviewIssues(rawOutput);
                    break;
                case AgentType.TestGeneration:
                    result.Changes = ParseCodeBlocks(rawOutput, "test");
                    break;
                case AgentType.Refactoring:
                    result.Changes = ParseCodeBlocks(rawOutput, "refactored");
                    break;
                case AgentType.Debug:
                    result.Issues = ParseCodeReviewIssues(rawOutput);
                    result.Changes = ParseCodeBlocks(rawOutput, "fix");
                    break;
                case AgentType.Documentation:
                    result.Changes = ParseCodeBlocks(rawOutput, "documentation");
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse structured output for task {TaskId}, returning raw", taskId);
        }

        return result;
    }

    private static string? ExtractJsonBlock(string text)
    {
        var match = JsonBlockRegex().Match(text);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private void ParseJsonBlock(AgentResult result, AgentType agentType, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Root array: treat as a direct issues/items list
        if (root.ValueKind == JsonValueKind.Array)
        {
            ParseIssueItems(result, root);
            _logger.LogDebug("Parsed JSON array block: {IssueCount} issues", result.Issues.Count);
            return;
        }

        // Root object: look for "issues" and "changes" properties
        if (root.TryGetProperty("issues", out var issuesArr) && issuesArr.ValueKind == JsonValueKind.Array)
            ParseIssueItems(result, issuesArr);

        if (root.TryGetProperty("changes", out var changesArr) && changesArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in changesArr.EnumerateArray())
            {
                result.Changes.Add(new FileChange
                {
                    FilePath = item.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? ""
                             : item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                    OriginalContent = item.TryGetProperty("original", out var orig) ? orig.GetString() ?? "" : "",
                    NewContent = item.TryGetProperty("newContent", out var nc) ? nc.GetString() ?? ""
                               : item.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                    Description = item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
                });
            }
        }

        _logger.LogDebug("Parsed JSON block: {IssueCount} issues, {ChangeCount} changes",
            result.Issues.Count, result.Changes.Count);
    }

    private static void ParseIssueItems(AgentResult result, JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            result.Issues.Add(new Issue
            {
                FilePath = item.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? ""
                         : item.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "",
                Line = item.TryGetProperty("line", out var ln) ? ln.GetInt32() : 0,
                Severity = ParseSeverity(item.TryGetProperty("severity", out var sev) ? sev.GetString() : "medium"),
                Message = item.TryGetProperty("message", out var msg) ? msg.GetString() ?? ""
                        : item.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : "",
                SuggestedFix = item.TryGetProperty("suggestedFix", out var fix) ? fix.GetString()
                             : item.TryGetProperty("fix", out var fx) ? fx.GetString() : null
            });
        }
    }

    private static List<Issue> ParseCodeReviewIssues(string text)
    {
        var issues = new List<Issue>();

        // Pattern: **severity** or [severity] followed by description, optionally with line numbers
        var matches = IssueLineRegex().Matches(text);
        foreach (Match match in matches)
        {
            var severity = ParseSeverity(match.Groups[1].Value);
            var lineStr = match.Groups[2].Value;
            var message = match.Groups[3].Value.Trim();

            issues.Add(new Issue
            {
                Line = int.TryParse(lineStr, out var line) ? line : 0,
                Severity = severity,
                Message = message
            });
        }

        // Also try bullet-point patterns: - Line 42: description
        if (issues.Count == 0)
        {
            var bulletMatches = BulletIssueRegex().Matches(text);
            foreach (Match match in bulletMatches)
            {
                issues.Add(new Issue
                {
                    Line = int.TryParse(match.Groups[1].Value, out var line) ? line : 0,
                    Severity = InferSeverity(match.Groups[2].Value),
                    Message = match.Groups[2].Value.Trim()
                });
            }
        }

        return issues;
    }

    private static List<FileChange> ParseCodeBlocks(string text, string context)
    {
        var changes = new List<FileChange>();
        var matches = CodeBlockRegex().Matches(text);

        foreach (Match match in matches)
        {
            var lang = match.Groups[1].Value;
            var code = match.Groups[2].Value.Trim();

            if (string.IsNullOrWhiteSpace(code)) continue;

            changes.Add(new FileChange
            {
                NewContent = code,
                Description = $"Generated {context} ({lang})"
            });
        }

        return changes;
    }

    private static IssueSeverity ParseSeverity(string? text)
    {
        if (text is null) return IssueSeverity.Medium;
        return text.ToLowerInvariant() switch
        {
            "critical" => IssueSeverity.Critical,
            "high" => IssueSeverity.High,
            "medium" or "moderate" => IssueSeverity.Medium,
            "low" or "minor" => IssueSeverity.Low,
            "info" or "note" or "suggestion" => IssueSeverity.Info,
            _ => IssueSeverity.Medium
        };
    }

    private static IssueSeverity InferSeverity(string message)
    {
        var lower = message.ToLowerInvariant();
        if (lower.Contains("security") || lower.Contains("injection") || lower.Contains("vulnerability"))
            return IssueSeverity.Critical;
        if (lower.Contains("memory leak") || lower.Contains("race condition") || lower.Contains("null reference"))
            return IssueSeverity.High;
        if (lower.Contains("performance") || lower.Contains("complexity"))
            return IssueSeverity.Medium;
        if (lower.Contains("style") || lower.Contains("naming") || lower.Contains("convention"))
            return IssueSeverity.Low;
        return IssueSeverity.Medium;
    }

    [GeneratedRegex(@"```json\s*\n([\s\S]*?)```", RegexOptions.Multiline)]
    private static partial Regex JsonBlockRegex();

    [GeneratedRegex(@"\*\*(\w+)\*\*.*?[Ll]ine\s*(\d+).*?[:\-–]\s*(.+)", RegexOptions.Multiline)]
    private static partial Regex IssueLineRegex();

    [GeneratedRegex(@"[-*]\s*[Ll]ine\s+(\d+)\s*[:\-–]\s*(.+)", RegexOptions.Multiline)]
    private static partial Regex BulletIssueRegex();

    [GeneratedRegex(@"```(\w*)\s*\n([\s\S]*?)```", RegexOptions.Multiline)]
    private static partial Regex CodeBlockRegex();
}
