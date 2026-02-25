using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Agents;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Extended unit tests for <see cref="ResultParser"/> covering root-array payloads,
/// changes extraction, severity mapping edge cases, and field aliases.
/// </summary>
public class ResultParserExtendedTests
{
    private readonly ResultParser _parser = new(NullLogger<ResultParser>.Instance);

    // ── JSON block extraction ─────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyOutput_ReturnsResultWithRawOutput()
    {
        var result = _parser.Parse("t1", AgentType.CodeReview, "", latencyMs: 100);

        Assert.Equal("t1", result.TaskId);
        Assert.True(result.Success);
        Assert.Equal("", result.Output);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Changes);
    }

    [Fact]
    public void Parse_PlainText_ReturnsRawOutputWithNoIssues()
    {
        const string raw = "Everything looks fine, no issues detected.";

        var result = _parser.Parse("t2", AgentType.CodeReview, raw, latencyMs: 50);

        Assert.Equal(raw, result.Output);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Parse_JsonBlock_WithIssuesArray_ExtractsIssues()
    {
        const string raw = """
            Here is my analysis:
            ```json
            {
              "issues": [
                {"filePath": "src/Foo.cs", "line": 42, "severity": "high", "message": "Null dereference risk"},
                {"filePath": "src/Bar.cs", "line": 7,  "severity": "low",  "message": "Unused variable"}
              ]
            }
            ```
            """;

        var result = _parser.Parse("t3", AgentType.CodeReview, raw, latencyMs: 200);

        Assert.Equal(2, result.Issues.Count);
        Assert.Equal("src/Foo.cs",        result.Issues[0].FilePath);
        Assert.Equal(42,                  result.Issues[0].Line);
        Assert.Equal(IssueSeverity.High,  result.Issues[0].Severity);
        Assert.Equal("Null dereference risk", result.Issues[0].Message);
        Assert.Equal(IssueSeverity.Low,   result.Issues[1].Severity);
    }

    [Fact]
    public void Parse_JsonBlock_RootArray_ExtractsIssues()
    {
        // LLM returns a root-level JSON array instead of wrapping object
        const string raw = """
            ```json
            [
              {"filePath": "src/Main.cs", "line": 1, "severity": "critical", "message": "SQL injection"},
              {"file": "src/Other.cs",   "line": 9, "severity": "medium",   "message": "Magic number"}
            ]
            ```
            """;

        var result = _parser.Parse("t4", AgentType.CodeReview, raw, latencyMs: 150);

        Assert.Equal(2, result.Issues.Count);
        Assert.Equal(IssueSeverity.Critical, result.Issues[0].Severity);
        Assert.Equal("SQL injection",        result.Issues[0].Message);
        Assert.Equal("src/Other.cs",         result.Issues[1].FilePath);
    }

    [Fact]
    public void Parse_JsonBlock_WithChangesArray_ExtractsChanges()
    {
        const string raw = """
            ```json
            {
              "changes": [
                {"filePath": "src/Foo.cs", "original": "old code", "newContent": "new code", "description": "refactored"},
                {"file": "src/Bar.cs", "content": "bar content", "description": "extracted method"}
              ]
            }
            ```
            """;

        var result = _parser.Parse("t5", AgentType.Refactoring, raw, latencyMs: 300);

        Assert.Equal(2, result.Changes.Count);
        Assert.Equal("src/Foo.cs",   result.Changes[0].FilePath);
        Assert.Equal("old code",     result.Changes[0].OriginalContent);
        Assert.Equal("new code",     result.Changes[0].NewContent);
        Assert.Equal("refactored",   result.Changes[0].Description);
        // Accepts "file" as alias for "filePath" and "content" for "newContent"
        Assert.Equal("src/Bar.cs",   result.Changes[1].FilePath);
        Assert.Equal("bar content",  result.Changes[1].NewContent);
    }

    [Fact]
    public void Parse_JsonBlock_WithBothIssuesAndChanges()
    {
        const string raw = """
            ```json
            {
              "issues": [{"severity": "info", "message": "style note"}],
              "changes": [{"filePath": "x.cs", "newContent": "new", "description": "fix"}]
            }
            ```
            """;

        var result = _parser.Parse("t6", AgentType.Debug, raw, latencyMs: 100);

        Assert.Single(result.Issues);
        Assert.Single(result.Changes);
    }

    // ── Severity parsing ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("critical", IssueSeverity.Critical)]
    [InlineData("high",     IssueSeverity.High)]
    [InlineData("medium",   IssueSeverity.Medium)]
    [InlineData("moderate", IssueSeverity.Medium)]
    [InlineData("low",      IssueSeverity.Low)]
    [InlineData("minor",    IssueSeverity.Low)]
    [InlineData("info",     IssueSeverity.Info)]
    [InlineData("note",     IssueSeverity.Info)]
    [InlineData("suggestion", IssueSeverity.Info)]
    [InlineData("unknown",  IssueSeverity.Medium)]  // default
    [InlineData("",         IssueSeverity.Medium)]  // empty → default
    public void Parse_JsonBlock_SeverityStrings_MapCorrectly(string severity, IssueSeverity expected)
    {
        var raw = $$"""
            ```json
            {"issues":[{"severity":"{{severity}}","message":"test"}]}
            ```
            """;

        var result = _parser.Parse("sev", AgentType.CodeReview, raw, latencyMs: 1);

        Assert.Equal(expected, result.Issues[0].Severity);
    }

    // ── Markdown fallback ─────────────────────────────────────────────────────

    [Fact]
    public void Parse_MarkdownCodeBlocks_TestGeneration_ExtractsChanges()
    {
        const string raw = """
            Here is the test:
            ```csharp
            [Fact]
            public void MyTest() { Assert.True(true); }
            ```
            """;

        var result = _parser.Parse("t7", AgentType.TestGeneration, raw, latencyMs: 10);

        Assert.Single(result.Changes);
        Assert.Contains("[Fact]", result.Changes[0].NewContent);
    }

    [Fact]
    public void Parse_MarkdownCodeBlocks_Refactoring_ExtractsChanges()
    {
        const string raw = """
            Refactored:
            ```csharp
            public int Add(int a, int b) => a + b;
            ```
            """;

        var result = _parser.Parse("t8", AgentType.Refactoring, raw, latencyMs: 10);

        Assert.Single(result.Changes);
        Assert.Contains("int Add", result.Changes[0].NewContent);
    }

    [Fact]
    public void Parse_MalformedJson_DoesNotThrow_ReturnsFallback()
    {
        // The JSON is inside a code block but is invalid
        const string raw = """
            ```json
            { "issues": [ bad json here
            ```
            """;

        // Should not throw — falls back gracefully
        var result = _parser.Parse("t9", AgentType.CodeReview, raw, latencyMs: 5);

        Assert.NotNull(result);
        Assert.Equal(raw, result.Output);
    }

    // ── Latency / metadata passthrough ────────────────────────────────────────

    [Fact]
    public void Parse_PreservesTaskIdAndLatency()
    {
        var result = _parser.Parse("my-task-id", AgentType.Generic, "output", latencyMs: 9999);

        Assert.Equal("my-task-id", result.TaskId);
        Assert.Equal(9999,         result.LatencyMs);
    }

    [Fact]
    public void Parse_ResultSuccessIsAlwaysTrue_WhenNoException()
    {
        var result = _parser.Parse("t", AgentType.CodeReview, "some output", latencyMs: 1);

        Assert.True(result.Success);
    }

    // ── SuggestedFix / alternate field names ─────────────────────────────────

    [Fact]
    public void Parse_JsonBlock_SuggestedFix_IsCaptured()
    {
        const string raw = """
            ```json
            {"issues":[{"severity":"high","message":"bug","suggestedFix":"Add null check"}]}
            ```
            """;

        var result = _parser.Parse("tf", AgentType.CodeReview, raw, latencyMs: 1);

        Assert.Equal("Add null check", result.Issues[0].SuggestedFix);
    }

    [Fact]
    public void Parse_JsonBlock_FixAlias_IsCaptured()
    {
        const string raw = """
            ```json
            {"issues":[{"severity":"low","message":"style","fix":"Use expression body"}]}
            ```
            """;

        var result = _parser.Parse("tf2", AgentType.CodeReview, raw, latencyMs: 1);

        Assert.Equal("Use expression body", result.Issues[0].SuggestedFix);
    }
}
