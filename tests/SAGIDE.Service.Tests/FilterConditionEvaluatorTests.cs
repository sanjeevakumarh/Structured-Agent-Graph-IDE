using SAGIDE.Service.Orchestrator;

namespace SAGIDE.Service.Tests;

/// <summary>
/// Tests for FilterConditionEvaluator.Filter() — the DAG router condition logic
/// that filters JSON arrays, section-delimited objects, and plain-text lines.
/// The class is internal, accessed here via InternalsVisibleTo (project reference).
/// </summary>
public class FilterConditionEvaluatorTests
{
    // ── JSON array — numeric operators ───────────────────────────────────────

    [Fact]
    public void Filter_JsonArray_LessEqual_ReturnsMatchingElements()
    {
        var input = """[{"symbol":"AAPL","pct_change":-6},{"symbol":"MSFT","pct_change":2},{"symbol":"TSLA","pct_change":-8}]""";
        var result = FilterConditionEvaluator.Filter(input, "pct_change <= -5");

        Assert.Contains("AAPL", result);
        Assert.Contains("TSLA", result);
        Assert.DoesNotContain("MSFT", result);
    }

    [Fact]
    public void Filter_JsonArray_GreaterThan_ReturnsMatchingElements()
    {
        var input = """[{"score":80},{"score":55},{"score":90}]""";
        var result = FilterConditionEvaluator.Filter(input, "score > 70");

        Assert.Contains("80", result);
        Assert.Contains("90", result);
        Assert.DoesNotContain("55", result);
    }

    [Fact]
    public void Filter_JsonArray_Equals_ReturnsExactMatch()
    {
        var input = """[{"status":"active"},{"status":"inactive"},{"status":"active"}]""";
        var result = FilterConditionEvaluator.Filter(input, "status == \"active\"");

        var sections = result.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, sections.Length);
    }

    [Fact]
    public void Filter_JsonArray_NotEquals_ExcludesValue()
    {
        var input = """[{"tier":"gold"},{"tier":"silver"},{"tier":"gold"}]""";
        var result = FilterConditionEvaluator.Filter(input, "tier != \"silver\"");

        Assert.DoesNotContain("silver", result);
        Assert.Contains("gold", result);
    }

    // ── JSON array — string operators ─────────────────────────────────────────

    [Fact]
    public void Filter_JsonArray_Contains_SubstringMatch()
    {
        var input = """[{"name":"Apple Inc"},{"name":"Microsoft Corp"},{"name":"AppDev LLC"}]""";
        var result = FilterConditionEvaluator.Filter(input, "name contains \"App\"");

        Assert.Contains("Apple Inc", result);
        Assert.Contains("AppDev LLC", result);
        Assert.DoesNotContain("Microsoft", result);
    }

    [Fact]
    public void Filter_JsonArray_NotContains_ExcludesSubstring()
    {
        var input = """[{"note":"error: missing"},{"note":"all good"},{"note":"warning only"}]""";
        var result = FilterConditionEvaluator.Filter(input, "note not_contains \"error\"");

        Assert.DoesNotContain("error: missing", result);
        Assert.Contains("all good", result);
        Assert.Contains("warning only", result);
    }

    // ── JSON array — snake_case fallback ─────────────────────────────────────

    [Fact]
    public void Filter_JsonArray_SnakeCaseFallback_ResolvesProperty()
    {
        // "pctChange" camelCase in condition → "pct_change" snake_case in JSON
        var input = """[{"pct_change":-3},{"pct_change":10}]""";
        var result = FilterConditionEvaluator.Filter(input, "pctChange < 0");

        Assert.Contains("-3", result);
        Assert.DoesNotContain("10", result);
    }

    // ── Limit ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_JsonArray_LimitApplied()
    {
        var input = """[{"v":1},{"v":2},{"v":3},{"v":4},{"v":5}]""";
        var result = FilterConditionEvaluator.Filter(input, "v >= 1", limit: 3);

        var sections = result.Split("\n---\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, sections.Length);
    }

    [Fact]
    public void Filter_NoCondition_LimitApplied()
    {
        var input = "line1\nline2\nline3\nline4\nline5";
        var result = FilterConditionEvaluator.Filter(input, condition: "", limit: 2);

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
    }

    // ── Section-delimited JSON objects ────────────────────────────────────────

    [Fact]
    public void Filter_SectionDelimited_FiltersJsonSections()
    {
        var input = "{\"score\":80}\n---\n{\"score\":40}\n---\n{\"score\":90}";
        var result = FilterConditionEvaluator.Filter(input, "score >= 75");

        Assert.Contains("80", result);
        Assert.Contains("90", result);
        Assert.DoesNotContain("40", result);
    }

    // ── Plain-text line filter ────────────────────────────────────────────────

    [Fact]
    public void Filter_PlainText_Contains_ReturnsMatchingLines()
    {
        var input = "error: file not found\ninfo: service started\nerror: connection refused";
        var result = FilterConditionEvaluator.Filter(input, "line contains \"error\"");

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Contains("error", l));
    }

    [Fact]
    public void Filter_PlainText_Equals_ExactLineMatch()
    {
        var input = "ok\nfail\nok\nskip";
        var result = FilterConditionEvaluator.Filter(input, "line == \"ok\"");

        var lines = result.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, l => Assert.Equal("ok", l));
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Fact]
    public void Filter_EmptyInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FilterConditionEvaluator.Filter(string.Empty, "score > 0"));
    }

    [Fact]
    public void Filter_WhitespaceInput_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, FilterConditionEvaluator.Filter("   ", "score > 0"));
    }

    [Fact]
    public void Filter_InvalidConditionSyntax_PassThrough()
    {
        const string input = "some data\nmore data";
        // "THIS IS NOT A VALID CONDITION" won't parse → pass-through
        var result = FilterConditionEvaluator.Filter(input, "THIS IS NOT A VALID CONDITION");
        Assert.Equal(input, result);
    }

    [Fact]
    public void Filter_EmptyCondition_PassThrough()
    {
        const string input = "line1\nline2";
        var result = FilterConditionEvaluator.Filter(input, condition: "");
        Assert.Equal(input, result);
    }

    [Fact]
    public void Filter_NoneMatch_ReturnsEmpty()
    {
        var input = """[{"score":10},{"score":20}]""";
        var result = FilterConditionEvaluator.Filter(input, "score > 100");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Filter_JsonArray_MissingField_NotIncluded()
    {
        // Elements without the field should be excluded, not throw
        var input = """[{"score":80},{"name":"no score"},{"score":50}]""";
        var result = FilterConditionEvaluator.Filter(input, "score > 60");

        Assert.Contains("80", result);
        Assert.DoesNotContain("no score", result);
        Assert.DoesNotContain("50", result);
    }
}
